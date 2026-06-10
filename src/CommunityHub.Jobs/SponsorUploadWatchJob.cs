using CommunityHub.Core.Integrations;
using Microsoft.Azure.Functions.Worker;
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
    private readonly ILogger<SponsorUploadWatchJob> _log;

    public SponsorUploadWatchJob(
        SponsorUploadWatchService watch,
        ILogger<SponsorUploadWatchJob> log)
    {
        _watch = watch;
        _log = log;
    }

    /// <summary>Every 15 minutes. NCRONTAB: sec min hour day month weekday.</summary>
    [Function("SponsorUploadWatchJob")]
    public async Task Run(
        [TimerTrigger("0 */15 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var result = await _watch.RunAsync(ct);
        _log.LogInformation(
            "SponsorUploadWatchJob: {Loc} folders, {Obs} files, {New} new, {Chg} changed, {Sent} mails, {Err} errors.",
            result.LocationsChecked, result.FilesObserved, result.FilesNew,
            result.FilesChanged, result.NotificationsSent, result.Errors);
    }
}
