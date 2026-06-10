using CommunityHub.Core.Integrations;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// Daily timer that triggers the sponsor-order pull. All real work lives in
/// <see cref="SponsorOrderPullService"/> in Core so the same engine can also
/// be invoked from the CommunityHub.OneShot CLI for local DEV verification
/// without deploying the Function App.
/// </summary>
public sealed class WooCommercePullJob
{
    private readonly SponsorOrderPullService _service;
    private readonly ILogger<WooCommercePullJob> _log;

    public WooCommercePullJob(
        SponsorOrderPullService service,
        ILogger<WooCommercePullJob> log)
    {
        _service = service;
        _log = log;
    }

    /// <summary>Daily at 03:00 UTC (matches scheduledJobs.woocommercePull cron).</summary>
    [Function("WooCommercePullJob")]
    public async Task Run(
        [TimerTrigger("0 0 3 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var result = await _service.RunAsync(ct);
        if (!result.RanToCompletion)
        {
            _log.LogWarning(
                "WooCommercePullJob: skipped ({Reason}).", result.SkipReason);
        }
    }
}
