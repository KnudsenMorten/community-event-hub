using CommunityHub.Core.Audit;
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
    private readonly IAuditTrail _audit;
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
        IAuditTrail audit,
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
        _audit = audit;
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

        // Master classes are now booked IN-HUB (CEH MasterClassSignups), NOT Zoho
        // Bookings — so we no longer pull Zoho appointments. We still pull Zoho TICKETS
        // to know who holds a 2-day ticket (the only eligibility source).
        var tickets = await _zoho.GetBackstageTicketsAsync(token, ct);

        var results = _reconciler.Reconcile(
            tickets, Array.Empty<ZohoAppointment>(),
            _options.TwoDayTicketNameRegex,
            _options.BookingServiceNameRegex);

        var now = _clock.GetUtcNow();

        // 1) Upsert each reconciled attendee with their Zoho TICKET status.
        foreach (var r in results)
        {
            var ticketStatus = r.HasTwoDayTicket
                ? TicketStatus.TwoDay
                : r.TicketClassName is not null ? TicketStatus.Other : TicketStatus.None;
            await UpsertAttendeeAsync(eventId, r, ticketStatus, now, ct);
        }
        await _db.SaveChangesAsync(ct);

        // 2) Resolve the CEH master-class SELECTION per attendee (a CONFIRMED in-hub
        //    seat) and reflect it onto the attendee row.
        var confirmed = await _db.MasterClassSignups
            .Where(s => s.EventId == eventId && s.Status == MasterClassSignupStatus.Confirmed)
            .Select(s => new { s.AttendeeId, Title = s.Session.Title })
            .ToListAsync(ct);
        var selectionByAttendee = confirmed
            .GroupBy(x => x.AttendeeId)
            .ToDictionary(g => g.Key, g => g.First().Title);

        // 3) Chase 2-day-ticket holders who still haven't picked a master class.
        //    (Duplicate-booking and missing-2-day-ticket chasers were removed
        //    2026-06-24: one-seat in-hub signup makes duplicates impossible, and the
        //    pull is already filtered to 2-day buyers — so the only remaining nudge is
        //    "you bought a 2-day ticket but haven't selected your master class yet".)
        var twoDay = await _db.Attendees
            .Where(a => a.EventId == eventId && a.TicketStatus == TicketStatus.TwoDay)
            .ToListAsync(ct);
        var due = new List<ReminderMessage>();
        foreach (var a in twoDay)
        {
            var hasSelection = selectionByAttendee.TryGetValue(a.Id, out var mcTitle);
            a.BookingStatus = hasSelection ? MasterClassBookingStatus.Booked : MasterClassBookingStatus.NotBooked;
            a.MasterClassName = hasSelection ? mcTitle : null;
            a.HasReconciliationMismatch = !hasSelection;
            a.LastSyncedAt = now;
            if (hasSelection) continue;

            var tokens = _templates.NewTokenSet();
            tokens["firstName"] = string.IsNullOrWhiteSpace(a.FirstName) ? "there" : a.FirstName;
            tokens["eventDisplayName"] = activeEvent.DisplayName;
            var rendered = _templates.Render("pending-master-class-selection", tokens);
            due.Add(new ReminderMessage(
                a.Email, "pending-master-class-selection", $"pendingmc:{a.Email}",
                rendered.Subject, rendered.HtmlBody));
        }

        await _db.SaveChangesAsync(ct);
        var sent = await _engine.SendDueAsync(eventId, due, ct);

        _log.LogInformation(
            "AttendeeReconcileJob: {Count} attendees, {Sent} chasers sent.",
            results.Count, sent);

        // Named Engine event (REQUIREMENTS §24) — the reconcile RUN summary. (Chaser
        // emails are separately captured as Email events.) Only when it processed
        // attendees or sent chasers.
        if (results.Count > 0 || sent > 0)
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = eventId,
                Category = AuditCategory.Engine,
                Action = "attendee-reconcile",
                ActorEmail = "system",
                Source = AuditSource.Job,
                Outcome = AuditOutcome.Success,
                Summary = $"Attendee reconcile: {results.Count} attendees, {sent} chaser(s) sent",
            }, ct);
    }

    private async Task UpsertAttendeeAsync(
        int eventId, AttendeeReconResult r, TicketStatus ticketStatus,
        DateTimeOffset now, CancellationToken ct)
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
        attendee.LastSyncedAt = now;
        // BookingStatus / MasterClassName / mismatch are set from the CEH master-class
        // selection pass in Run (in-hub signup is the source of truth now).
    }
}
