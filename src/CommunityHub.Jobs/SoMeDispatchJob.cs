using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// The LinkedIn company-page SoMe scheduling-queue dispatcher (REQUIREMENTS §19) —
/// the "social media calendar" job. Unlike the daily reminder job, this runs on a
/// short cadence (every 5 minutes) because posts fire at an exact scheduled time
/// and the T-5-minute speaker pre-alert needs ~5-minute granularity.
///
/// Each run, for every active edition, it asks <see cref="SoMeDispatchService"/>
/// to: (1) email the T-5 speaker pre-alert for soon-due Speaker posts, and
/// (2) publish every DUE, Active, Queued post through the gated LinkedIn
/// publisher. It is idempotent — a published post is marked Published (the
/// sent-marker), so a re-run never double-posts; a missed run self-heals on the
/// next pass. When posting is not configured (disabled / no page / Null
/// publisher) due posts are left Queued and nothing is faked.
/// </summary>
public sealed class SoMeDispatchJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SoMeDispatchService _dispatch;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly ILogger<SoMeDispatchJob> _log;

    public SoMeDispatchJob(
        CommunityHubDbContext db,
        SoMeDispatchService dispatch,
        FeatureGateService gate,
        IAuditTrail audit,
        ILogger<SoMeDispatchJob> log)
    {
        _db = db;
        _dispatch = dispatch;
        _gate = gate;
        _audit = audit;
        _log = log;
    }

    /// <summary>Every 5 minutes. NCRONTAB: sec min hour day month weekday.</summary>
    [Function("SoMeDispatchJob")]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var activeEventIds = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(ct);

        foreach (var eventId in activeEventIds)
        {
            // GATE (REQUIREMENTS §23): SoMe scheduling is an advanced feature, off
            // by default. When disabled for this edition the dispatcher no-ops —
            // no posts published, no pre-alerts sent.
            if (!await _gate.IsFeatureEnabledAsync("some-scheduling", eventId, ct))
            {
                _log.LogInformation(
                    "SoMeDispatchJob: event {EventId} — feature 'some-scheduling' disabled, skipped.",
                    eventId);
                continue;
            }

            var result = await _dispatch.DispatchDueAsync(eventId, ct);
            _log.LogInformation(
                "SoMeDispatchJob: event {EventId} — {Published} published, {Failed} failed, "
                + "{Skipped} skipped, {PreAlerts} pre-alert(s). {Message}",
                eventId, result.Published, result.Failed, result.Skipped,
                result.PreAlertsSent, result.Message);

            // Named Engine event (REQUIREMENTS §24) — only when this 5-min run actually
            // published/failed/pre-alerted (idle runs would flood the trail).
            if (result.Published + result.Failed + result.PreAlertsSent > 0)
                await _audit.RecordAsync(new AuditEntry
                {
                    EventId = eventId,
                    Category = AuditCategory.Engine,
                    Action = "some-scheduling",
                    ActorEmail = "system",
                    Source = AuditSource.Job,
                    Outcome = result.Failed > 0 ? AuditOutcome.Failure : AuditOutcome.Success,
                    Summary = $"SoMe dispatch: {result.Published} published, {result.Failed} failed, "
                        + $"{result.PreAlertsSent} pre-alert(s)",
                }, ct);
        }
    }
}
