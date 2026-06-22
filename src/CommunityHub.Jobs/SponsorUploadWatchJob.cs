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
/// Polls every provisioned per-sponsor SharePoint upload folder every 15
/// minutes, diffs the current file listing against the last-known state
/// (SponsorUploadFile table), and emails the configured recipients when a
/// sponsor adds or replaces a file. The pull engine
/// (<see cref="SponsorOrderPullService"/>) is what registers the folders +
/// recipients in the first place; this job just watches them.
/// </summary>
public sealed class SponsorUploadWatchJob
{
    private readonly SponsorUploadWatchService _watch;
    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly ILogger<SponsorUploadWatchJob> _log;

    public SponsorUploadWatchJob(
        SponsorUploadWatchService watch,
        CommunityHubDbContext db,
        FeatureGateService gate,
        IAuditTrail audit,
        ILogger<SponsorUploadWatchJob> log)
    {
        _watch = watch;
        _db = db;
        _gate = gate;
        _audit = audit;
        _log = log;
    }

    /// <summary>Every 15 minutes. NCRONTAB: sec min hour day month weekday.</summary>
    [Function("SponsorUploadWatchJob")]
    public async Task Run(
        [TimerTrigger("0 */15 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        // GATE (REQUIREMENTS §23): the sponsor upload watcher is an advanced
        // feature, off by default. The watcher is fleet-wide (not edition-scoped),
        // so it runs only while at least one active edition has
        // 'sponsor-upload-watch' enabled; when every active edition has it off the
        // job no-ops (no SharePoint polling, no notification emails).
        var activeEventIds = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(ct);
        var anyEnabled = false;
        foreach (var id in activeEventIds)
        {
            if (await _gate.IsFeatureEnabledAsync("sponsor-upload-watch", id, ct))
            {
                anyEnabled = true;
                break;
            }
        }
        if (!anyEnabled)
        {
            _log.LogInformation(
                "SponsorUploadWatchJob: feature 'sponsor-upload-watch' disabled for all active editions, skipped.");
            return;
        }

        var result = await _watch.RunAsync(ct);
        _log.LogInformation(
            "SponsorUploadWatchJob: {Loc} folders, {Obs} files, {New} new, {Chg} changed, {Sent} mails, {Err} errors.",
            result.LocationsChecked, result.FilesObserved, result.FilesNew,
            result.FilesChanged, result.NotificationsSent, result.Errors);

        // Named Engine event (REQUIREMENTS §24) — only when something actually changed
        // or errored (idle 15-min polls would flood the trail).
        if (result.FilesNew + result.FilesChanged + result.NotificationsSent + result.Errors > 0)
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = activeEventIds.FirstOrDefault(),
                Category = AuditCategory.Engine,
                Action = "sponsor-upload-watch",
                ActorEmail = "system",
                Source = AuditSource.Job,
                Outcome = result.Errors > 0 ? AuditOutcome.Failure : AuditOutcome.Success,
                Summary = $"Sponsor uploads: {result.FilesNew} new, {result.FilesChanged} changed, "
                    + $"{result.NotificationsSent} mail(s), {result.Errors} error(s)",
            }, ct);
    }
}
