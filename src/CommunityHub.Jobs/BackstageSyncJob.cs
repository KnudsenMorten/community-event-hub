using CommunityHub.Core.Integrations;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// The Backstage exhibitor sync job (CONTEXT.md - Backstage exhibitor sync).
/// Daily, it derives the sponsor/exhibitor list from the completed WooCommerce
/// orders, then runs <see cref="BackstageSyncService"/>: each exhibitor is
/// checked against Backstage and, if missing, created (when the API allows)
/// with the event coordinator emailed.
///
/// In TESTMODE the only exhibitor examined is the configured test sponsor and
/// no real Backstage calls or live coordinator emails happen - the run
/// exercises the whole flow safely.
/// </summary>
public sealed class BackstageSyncJob
{
    private readonly WooCommerceClient _woo;
    private readonly BackstageSyncService _sync;
    private readonly BackstageSyncOptions _options;
    private readonly TestModeOptions _testMode;
    private readonly ILogger<BackstageSyncJob> _log;

    public BackstageSyncJob(
        WooCommerceClient woo,
        BackstageSyncService sync,
        BackstageSyncOptions options,
        TestModeOptions testMode,
        ILogger<BackstageSyncJob> log)
    {
        _woo = woo;
        _sync = sync;
        _options = options;
        _testMode = testMode;
        _log = log;
    }

    /// <summary>Daily at 06:30 UTC - after the WooCommerce pull (06:00).</summary>
    [Function("BackstageSyncJob")]
    public async Task Run(
        [TimerTrigger("0 30 6 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("BackstageSyncJob: disabled by config.");
            return;
        }

        IReadOnlyList<ExhibitorRecord> exhibitors;

        if (_testMode.Enabled)
        {
            // TESTMODE: examine only the configured test sponsor.
            exhibitors = new[]
            {
                new ExhibitorRecord(
                    _testMode.TestSponsorCompanyId,
                    _testMode.TestSponsorName,
                    _testMode.TestSponsorEmail),
            };
            _log.LogInformation(
                "BackstageSyncJob: TESTMODE - examining test sponsor '{Name}'.",
                _testMode.TestSponsorName);
        }
        else
        {
            // Live: derive distinct exhibitors from completed WooCommerce
            // orders. Each order carries the Company Manager company id and
            // billing company; an order with no company id is skipped.
            var orders = await _woo.GetOrdersAsync("completed", ct);
            exhibitors = orders
                .Where(o => !string.IsNullOrWhiteSpace(o.CompanyId))
                .GroupBy(o => o.CompanyId!)
                .Select(g =>
                {
                    var first = g.First();
                    return new ExhibitorRecord(
                        g.Key,
                        first.BillingCompany,
                        first.BillingEmail);
                })
                .ToList();
            _log.LogInformation(
                "BackstageSyncJob: {Count} distinct exhibitor(s) from orders.",
                exhibitors.Count);
        }

        var result = await _sync.SyncAsync(exhibitors, ct);
        _log.LogInformation(
            "BackstageSyncJob done: {Examined} examined, {Created} created, "
            + "{Would} flagged to coordinator, {Failed} failed.",
            result.Examined, result.Created, result.WouldCreate, result.Failed);
    }
}
