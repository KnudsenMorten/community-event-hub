using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// The attendee reconciliation job (CONTEXT.md 9z). Runs daily: pulls Zoho
/// Backstage tickets + Zoho Bookings appointments, reconciles them, upserts
/// the Attendee table, and sends the three chaser reminders through the
/// ReminderEngine (so they dedup, unlike the source PowerShell which re-sent
/// daily). Gated by integrations.zoho.enabled.
/// </summary>
public sealed class AttendeeReconcileJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _options;
    private readonly AttendeeReconciler _reconciler;
    private readonly ReminderEngine _engine;
    private readonly TimeProvider _clock;
    private readonly ILogger<AttendeeReconcileJob> _log;

    public AttendeeReconcileJob(
        CommunityHubDbContext db,
        ZohoClient zoho,
        ZohoOptions options,
        AttendeeReconciler reconciler,
        ReminderEngine engine,
        TimeProvider clock,
        ILogger<AttendeeReconcileJob> log)
    {
        _db = db;
        _zoho = zoho;
        _options = options;
        _reconciler = reconciler;
        _engine = engine;
        _clock = clock;
        _log = log;
    }

    /// <summary>Daily at 07:00 UTC.</summary>
    [Function("AttendeeReconcileJob")]
    public async Task Run(
        [TimerTrigger("0 0 7 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("AttendeeReconcileJob: disabled by config.");
            return;
        }

        var activeEventId = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (activeEventId is null)
        {
            _log.LogWarning("AttendeeReconcileJob: no active event.");
            return;
        }
        var eventId = activeEventId.Value;

        var token = await _zoho.GetAccessTokenAsync(ct);
        if (token is null)
        {
            _log.LogError("AttendeeReconcileJob: could not get Zoho token.");
            return;
        }

        var tickets = await _zoho.GetBackstageTicketsAsync(token, ct);
        var appointments = await _zoho.GetBookingsAppointmentsAsync(token, ct);

        var results = _reconciler.Reconcile(
            tickets, appointments,
            _options.TwoDayTicketNameRegex,
            _options.BookingServiceNameRegex);

        var now = _clock.GetUtcNow();
        var due = new List<ReminderMessage>();

        foreach (var r in results)
        {
            var (ticketStatus, bookingStatus, mismatch) = Evaluate(r);

            await UpsertAttendeeAsync(eventId, r, ticketStatus,
                bookingStatus, mismatch, now, ct);

            // Build whichever chaser applies.
            var chaser = BuildChaser(r, ticketStatus, bookingStatus);
            if (chaser is not null)
            {
                due.Add(chaser);
            }
        }

        await _db.SaveChangesAsync(ct);
        var sent = await _engine.SendDueAsync(eventId, due, ct);

        _log.LogInformation(
            "AttendeeReconcileJob: {Count} attendees, {Sent} chasers sent.",
            results.Count, sent);
    }

    private static (TicketStatus, MasterClassBookingStatus, bool) Evaluate(
        AttendeeReconResult r)
    {
        var ticketStatus = r.HasTwoDayTicket
            ? TicketStatus.TwoDay
            : r.TicketClassName is not null ? TicketStatus.Other : TicketStatus.None;

        var bookingStatus = r.MasterClassBookingCount switch
        {
            0 => MasterClassBookingStatus.NotBooked,
            1 => MasterClassBookingStatus.Booked,
            _ => MasterClassBookingStatus.MultipleBookings,
        };

        // A mismatch is: 2-day ticket but no booking; OR a booking but no
        // 2-day ticket; OR more than one booking.
        var mismatch =
            (ticketStatus == TicketStatus.TwoDay
             && bookingStatus == MasterClassBookingStatus.NotBooked)
            || (bookingStatus != MasterClassBookingStatus.NotBooked
                && ticketStatus != TicketStatus.TwoDay)
            || bookingStatus == MasterClassBookingStatus.MultipleBookings;

        return (ticketStatus, bookingStatus, mismatch);
    }

    private async Task UpsertAttendeeAsync(
        int eventId, AttendeeReconResult r,
        TicketStatus ticketStatus, MasterClassBookingStatus bookingStatus,
        bool mismatch, DateTimeOffset now, CancellationToken ct)
    {
        var attendee = await _db.Attendees.FirstOrDefaultAsync(
            a => a.EventId == eventId && a.Email == r.Email, ct);

        if (attendee is null)
        {
            attendee = new Attendee
            {
                EventId = eventId,
                Email = r.Email,
                CreatedAt = now,
            };
            _db.Attendees.Add(attendee);
        }

        attendee.FirstName = r.FirstName;
        attendee.LastName = r.LastName;
        attendee.TicketStatus = ticketStatus;
        attendee.TicketClassName = r.TicketClassName;
        attendee.BookingStatus = bookingStatus;
        attendee.MasterClassName = r.MasterClassNames.Count > 0
            ? string.Join(", ", r.MasterClassNames)
            : null;
        attendee.HasReconciliationMismatch = mismatch;
        attendee.LastSyncedAt = now;
    }

    /// <summary>
    /// The chaser for this attendee, or null if nothing to chase. The
    /// OccasionKey is stable per mismatch type so the engine dedups.
    /// </summary>
    private static ReminderMessage? BuildChaser(
        AttendeeReconResult r,
        TicketStatus ticketStatus,
        MasterClassBookingStatus bookingStatus)
    {
        var first = string.IsNullOrWhiteSpace(r.FirstName) ? "there" : r.FirstName;

        // 3. Duplicate booking.
        if (bookingStatus == MasterClassBookingStatus.MultipleBookings)
        {
            var list = string.Join(", ", r.MasterClassNames);
            return new ReminderMessage(
                r.Email, "attendee-duplicate-booking", $"dup:{r.Email}",
                "Issue detected with your Master Class booking",
                $"<p>Hi {Enc(first)},</p><p>You are registered for more than " +
                $"one Master Class: {Enc(list)}. Please keep one and cancel " +
                "the others in Zoho Bookings.</p>");
        }

        // 1. Has 2-day ticket, no booking.
        if (ticketStatus == TicketStatus.TwoDay
            && bookingStatus == MasterClassBookingStatus.NotBooked)
        {
            return new ReminderMessage(
                r.Email, "attendee-missing-booking", $"nobooking:{r.Email}",
                "Reserve your Master Class seat",
                $"<p>Hi {Enc(first)},</p><p>You have a 2-day ticket but have " +
                "not yet reserved a Master Class seat. Seats are first-come, " +
                "first-served - please book soon.</p>");
        }

        // 2. Has a booking, no 2-day ticket.
        if (bookingStatus != MasterClassBookingStatus.NotBooked
            && ticketStatus != TicketStatus.TwoDay)
        {
            return new ReminderMessage(
                r.Email, "attendee-missing-ticket", $"noticket:{r.Email}",
                "Complete your 2-day ticket purchase",
                $"<p>Hi {Enc(first)},</p><p>You reserved a Master Class seat " +
                "but we have no 2-day ticket for this email. A 2-day ticket " +
                "is required to attend. If you bought it under a different " +
                "email, please contact us.</p>");
        }

        return null;
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
}
