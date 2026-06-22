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
/// Timer that triggers the sponsor-order pull every 30 minutes. All real work
/// lives in <see cref="SponsorOrderPullService"/> in Core so the same engine can
/// also be invoked from the CommunityHub.OneShot CLI for local DEV verification
/// without deploying the Function App. The pull covers sponsor orders + the
/// sponsor-contact sync in one job, gated by the 'sponsor-order-pull' feature flag.
/// </summary>
public sealed class WooCommercePullJob
{
    private readonly SponsorOrderPullService _service;
    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly ILogger<WooCommercePullJob> _log;

    public WooCommercePullJob(
        SponsorOrderPullService service,
        CommunityHubDbContext db,
        FeatureGateService gate,
        IAuditTrail audit,
        ILogger<WooCommercePullJob> log)
    {
        _service = service;
        _db = db;
        _gate = gate;
        _audit = audit;
        _log = log;
    }

    /// <summary>Every 30 minutes (matches scheduledJobs.woocommercePull cron).</summary>
    [Function("WooCommercePullJob")]
    public async Task Run(
        [TimerTrigger("0 */30 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        // GATE (REQUIREMENTS §23): the sponsor-order pull is an advanced feature,
        // off by default. The pull engine is fleet-wide (not edition-scoped), so it
        // runs only while at least one active edition has 'sponsor-order-pull'
        // enabled; when every active edition has it off the job no-ops.
        var activeEventIds = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(ct);
        var anyEnabled = false;
        foreach (var id in activeEventIds)
        {
            if (await _gate.IsFeatureEnabledAsync("sponsor-order-pull", id, ct))
            {
                anyEnabled = true;
                break;
            }
        }
        if (!anyEnabled)
        {
            _log.LogInformation(
                "WooCommercePullJob: feature 'sponsor-order-pull' disabled for all active editions, skipped.");
            return;
        }

        var result = await _service.RunAsync(ct);
        if (!result.RanToCompletion)
        {
            _log.LogWarning(
                "WooCommercePullJob: skipped ({Reason}).", result.SkipReason);
        }

        // Named Engine event (REQUIREMENTS §24). Fleet-wide pull — record under the
        // active edition (CEH runs one active edition at a time). Only audit a run
        // that did something (or failed) — idle 30-min polls would flood the trail.
        var changed = result.OrdersFetched + result.TasksCreated
            + result.ContactsCreated + result.ContactsUpdated;
        if (changed > 0 || !result.RanToCompletion)
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = activeEventIds.FirstOrDefault(),
                Category = AuditCategory.Engine,
                Action = "sponsor-order-pull",
                ActorEmail = "system",
                Source = AuditSource.Job,
                Outcome = result.RanToCompletion ? AuditOutcome.Success : AuditOutcome.Failure,
                Summary = result.RanToCompletion
                    ? $"Sponsor order pull: {result.OrdersFetched} orders, {result.TasksCreated} tasks, "
                        + $"contacts +{result.ContactsCreated}/~{result.ContactsUpdated}"
                    : $"Sponsor order pull skipped: {result.SkipReason}",
            }, ct);
    }
}
