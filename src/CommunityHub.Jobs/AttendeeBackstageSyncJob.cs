using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// THE single authoritative one-way Zoho→CEH sync (REQUIREMENTS §125/§126/§128). Pulls
/// the FULL Backstage dataset — orders + every ticket/attendee (v3, enriched), not just
/// 2-day — and reconciles the local mirror to match Zoho's ACTIVE set exactly, keyed on
/// the ticket id (§6):
/// <list type="bullet">
/// <item>UPSERT orders + attendees; Master-Class eligibility (TicketStatus) is set via the
/// single <see cref="MasterClassTicketPolicy"/> only.</item>
/// <item>A reassigned ticket transfers its Master Class to the new holder (who is emailed
/// to validate).</item>
/// <item>SOFT-CANCEL (§128): a ticket/order gone from the pull is marked Cancelled (seat
/// released → waitlist promoted + notified, history kept). A reappearance flips to Active.</item>
/// <item>Folds the former AttendeeReconcileJob's still-needed behaviour — the
/// "2-day ticket but no Master Class selected" chaser — so there is ONE writer.</item>
/// <item>Records a last-successful-sync marker (<see cref="SyncRun"/>) for telemetry (§127).</item>
/// </list>
/// CEH NEVER writes/deletes anything in Zoho. Gated by the <c>attendee-reconcile</c>
/// feature + <c>zoho.enabled</c>. Runs hourly.
/// </summary>
public sealed class AttendeeBackstageSyncJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _options;
    private readonly AttendeeTicketSyncService _sync;
    private readonly MasterClassEmailService _mcEmail;
    private readonly MasterClassPromotionEmailService _promo;
    private readonly AttendeeWelcomeProvisioningService _provisioning;
    private readonly WelcomeWithLoginEmailService _welcome;
    private readonly ReminderEngine _engine;
    private readonly EmailTemplateProvider _templates;
    private readonly IAuditTrail _audit;
    private readonly TimeProvider _clock;
    private readonly FeatureGateService _gate;
    private readonly IConfiguration _config;
    private readonly ILogger<AttendeeBackstageSyncJob> _log;

    public AttendeeBackstageSyncJob(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions options,
        AttendeeTicketSyncService sync, MasterClassEmailService mcEmail,
        MasterClassPromotionEmailService promo,
        AttendeeWelcomeProvisioningService provisioning,
        WelcomeWithLoginEmailService welcome,
        ReminderEngine engine, EmailTemplateProvider templates, IAuditTrail audit,
        TimeProvider clock, FeatureGateService gate,
        IConfiguration config, ILogger<AttendeeBackstageSyncJob> log)
    {
        _db = db; _zoho = zoho; _options = options; _sync = sync;
        _mcEmail = mcEmail; _promo = promo; _provisioning = provisioning;
        _welcome = welcome; _engine = engine; _templates = templates; _audit = audit;
        _clock = clock; _gate = gate; _config = config; _log = log;
    }

    [Function("AttendeeBackstageSyncJob")]
    public async Task Run([TimerTrigger("0 20 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (!_options.Enabled) { _log.LogInformation("AttendeeBackstageSyncJob: Zoho disabled."); return; }

        var activeEvent = await _db.Events.Where(e => e.IsActive)
            .Select(e => new { e.Id, e.DisplayName }).FirstOrDefaultAsync(ct);
        if (activeEvent is null) { _log.LogWarning("AttendeeBackstageSyncJob: no active event."); return; }
        var eventId = activeEvent.Id;
        if (!await _gate.IsFeatureEnabledAsync("attendee-reconcile", eventId, ct))
        { _log.LogInformation("AttendeeBackstageSyncJob: feature off."); return; }

        var token = await _zoho.GetAccessTokenAsync(ct);
        if (token is null) { _log.LogError("AttendeeBackstageSyncJob: no Zoho token."); return; }

        // FULL dataset pull: orders + attendees (every ticket class, §125).
        var orders = (await _zoho.GetBackstageOrdersAsync(token, ct))
            .Select(AttendeeTicketSyncService.FromBackstageOrder).ToList();
        var attendees = await _zoho.GetBackstageAttendeesAsync(token, ct);
        var rows = attendees.Select(AttendeeTicketSyncService.FromBackstage).ToList();

        var result = await _sync.SyncAsync(eventId, rows, orders, ct);

        var domain = _config["Hub:CustomDomain"];
        var baseUrl = string.IsNullOrWhiteSpace(domain) ? "https://eldk27.eventhub.expertslive.dk" : $"https://{domain}";

        var reEmails = 0;
        foreach (var r in result.Reassignments)
            try { if (await _mcEmail.SendReassignmentValidationAsync(r.AttendeeId, r.InheritedMcTitle, baseUrl, ct)) reEmails++; } catch { }
        var promoEmails = 0;
        foreach (var p in result.FreedPromotions)
            if (p.PromotedSignupId is int id)
                try { if (await _promo.SendPromotionAsync(id, baseUrl, ct)) promoEmails++; } catch { }

        // --- Folded chaser (was AttendeeReconcileJob): "you hold a 2-day ticket but
        //     haven't selected your Master Class yet". One writer now (§125). Only the
        //     ACTIVE mirror set is chased — soft-cancelled rows are excluded. ---
        var chasers = await ChaseUnselectedTwoDayAsync(eventId, activeEvent.DisplayName, ct);

        // --- Last-successful-sync marker for the telemetry "Updated <t>" footer (§127) ---
        await RecordSyncMarkerAsync(eventId, result, ct);

        _log.LogInformation(
            "AttendeeBackstageSyncJob: {Orders} orders ({OC} new, {OX} cancelled), {Pulled} attendees — "
            + "created {C}, updated {U}, reassigned {R} ({RE} validated), cancelled {X} ({PE} promoted), "
            + "reactivated {RA}, {CH} chaser(s) sent.",
            orders.Count, result.OrdersCreated, result.OrdersCancelled, rows.Count,
            result.Created, result.Updated, result.Reassigned, reEmails, result.Cancelled, promoEmails,
            result.Reactivated, chasers);

        // Named Engine event (REQUIREMENTS §24) — the sync RUN summary.
        await _audit.RecordAsync(new AuditEntry
        {
            EventId = eventId,
            Category = AuditCategory.Engine,
            Action = "attendee-backstage-sync",
            ActorEmail = "system",
            Source = AuditSource.Job,
            Outcome = AuditOutcome.Success,
            Summary = $"Backstage sync: {result.OrdersActive} active orders, {result.AttendeesActive} active "
                + $"attendees (created {result.Created}, updated {result.Updated}, reassigned {result.Reassigned}, "
                + $"cancelled {result.Cancelled}, reactivated {result.Reactivated}); {chasers} chaser(s) sent",
        }, ct);

        // Attendee welcome auto-provisioning REMOVED (operator 2026-06-23): there is
        // no separate attendee welcome — the only attendee mail is the Master Class
        // confirmed-seat email (masterclass-confirmed), sent on seat confirmation.
    }

    /// <summary>
    /// Reflect each ACTIVE 2-day attendee's in-hub Master-Class selection onto their row
    /// (BookingStatus / MasterClassName / HasReconciliationMismatch) and send the
    /// "pending-master-class-selection" reminder to those who hold a 2-day ticket but
    /// have not selected a Master Class. Folded from the retired AttendeeReconcileJob so
    /// the mirror sync is the single writer (§125). Returns how many chasers were sent.
    /// </summary>
    private async Task<int> ChaseUnselectedTwoDayAsync(int eventId, string eventDisplayName, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // CONFIRMED in-hub seats per attendee (the only Master-Class source of truth).
        var confirmed = await _db.MasterClassSignups
            .Where(s => s.EventId == eventId && s.Status == MasterClassSignupStatus.Confirmed)
            .Select(s => new { s.AttendeeId, Title = s.Session.Title })
            .ToListAsync(ct);
        var selectionByAttendee = confirmed
            .GroupBy(x => x.AttendeeId)
            .ToDictionary(g => g.Key, g => g.First().Title);

        // Only ACTIVE 2-day holders (soft-cancelled rows are excluded from the chase, §128).
        var twoDay = await _db.Attendees
            .Where(a => a.EventId == eventId
                        && a.TicketStatus == TicketStatus.TwoDay
                        && a.MirrorState == MirrorState.Active)
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
            tokens["eventDisplayName"] = eventDisplayName;
            var rendered = _templates.Render("pending-master-class-selection", tokens);
            due.Add(new ReminderMessage(
                a.Email, "pending-master-class-selection", $"pendingmc:{a.Email}",
                rendered.Subject, rendered.HtmlBody));
        }

        await _db.SaveChangesAsync(ct);
        return await _engine.SendDueAsync(eventId, due, ct);
    }

    /// <summary>Upsert the per-edition last-successful-sync marker (§127) used by the
    /// telemetry "Updated &lt;t&gt;" footer, with the run's active/cancelled tallies.</summary>
    private async Task RecordSyncMarkerAsync(int eventId, AttendeeTicketSyncService.SyncResult r, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var marker = await _db.SyncRuns.FirstOrDefaultAsync(
            s => s.EventId == eventId && s.Key == SyncRun.AttendeeBackstageKey, ct);
        if (marker is null)
        {
            marker = new SyncRun { EventId = eventId, Key = SyncRun.AttendeeBackstageKey, CreatedAt = now };
            _db.SyncRuns.Add(marker);
        }
        marker.LastSuccessAt = now;
        marker.OrdersActive = r.OrdersActive;
        marker.OrdersCancelled = r.OrdersCancelled;
        marker.AttendeesActive = r.AttendeesActive;
        marker.AttendeesCancelled = r.Cancelled;
        marker.Summary = $"{r.OrdersActive} active orders / {r.AttendeesActive} active attendees "
            + $"(created {r.Created}, updated {r.Updated}, cancelled {r.Cancelled})";
        await _db.SaveChangesAsync(ct);
    }
}
