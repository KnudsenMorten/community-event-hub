using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
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
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly FeatureGateService _gate;
    private readonly ILogger<AttendeeReconcileJob> _log;

    public AttendeeReconcileJob(
        CommunityHubDbContext db,
        ZohoClient zoho,
        ZohoOptions options,
        AttendeeReconciler reconciler,
        ReminderEngine engine,
        EmailTemplateProvider templates,
        TimeProvider clock,
        FeatureGateService gate,
        ILogger<AttendeeReconcileJob> log)
    {
        _db = db;
        _zoho = zoho;
        _options = options;
        _reconciler = reconciler;
        _engine = engine;
        _templates = templates;
        _clock = clock;
        _gate = gate;
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

        var activeEvent = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (activeEvent is null)
        {
            _log.LogWarning("AttendeeReconcileJob: no active event.");
            return;
        }
        var eventId = activeEvent.Id;

        // GATE (REQUIREMENTS §23): attendee reconciliation is an advanced feature,
        // off by default. When disabled for this edition the job no-ops — no Zoho
        // pull, no attendee upserts, no chaser reminders sent.
        if (!await _gate.IsFeatureEnabledAsync("attendee-reconcile", eventId, ct))
        {
            _log.LogInformation(
                "AttendeeReconcileJob: event {EventId} — feature 'attendee-reconcile' disabled, skipped.",
                eventId);
            return;
        }

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
            var chaser = BuildChaser(r, ticketStatus, bookingStatus, activeEvent.DisplayName);
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
    /// Rendered through the branded EmailTemplateProvider (CONTEXT.md 11d)
    /// like every other reminder type — the inline-HTML bodies this method
    /// used to build were the last emails bypassing the template system.
    /// </summary>
    private ReminderMessage? BuildChaser(
        AttendeeReconResult r,
        TicketStatus ticketStatus,
        MasterClassBookingStatus bookingStatus,
        string eventDisplayName)
    {
        string? template = null;
        string? occasion = null;

        // Token values are HTML-encoded by the renderer at the seam
        // (EmailTemplateRenderer, REQUIREMENTS §10c-4) — pass raw text.
        var tokens = _templates.NewTokenSet();
        tokens["firstName"] = string.IsNullOrWhiteSpace(r.FirstName) ? "there" : r.FirstName;
        tokens["eventDisplayName"] = eventDisplayName;

        // 3. Duplicate booking.
        if (bookingStatus == MasterClassBookingStatus.MultipleBookings)
        {
            template = "attendee-duplicate-booking";
            occasion = $"dup:{r.Email}";
            tokens["masterClassList"] = string.Join(", ", r.MasterClassNames);
        }
        // 1. Has 2-day ticket, no booking.
        else if (ticketStatus == TicketStatus.TwoDay
                 && bookingStatus == MasterClassBookingStatus.NotBooked)
        {
            template = "attendee-missing-booking";
            occasion = $"nobooking:{r.Email}";
        }
        // 2. Has a booking, no 2-day ticket.
        else if (bookingStatus != MasterClassBookingStatus.NotBooked
                 && ticketStatus != TicketStatus.TwoDay)
        {
            template = "attendee-missing-ticket";
            occasion = $"noticket:{r.Email}";
        }

        if (template is null) return null;

        var rendered = _templates.Render(template, tokens);
        return new ReminderMessage(
            r.Email, template, occasion!, rendered.Subject, rendered.HtmlBody);
    }
}
