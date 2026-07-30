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
/// 🔒 <b>RETIRED (§641, operator 2026-07-29: *"retire BackstageSyncJob and delete its health
/// marker"*).</b> It has NO <c>[Function]</c>/<c>[TimerTrigger]</c> attribute and must never be
/// scheduled again. (Was: <c>"0 */5 * * * *"</c> base tick, §510 interval 10 minutes.)
/// </summary>
/// <remarks>
/// <para><b>Why it was retired rather than fixed.</b> §637 found it had been a **no-op for its
/// entire deployed life**: it requires <c>BackstageSync:Enabled</c> in config, which was never set
/// in PROD — while the <c>backstage-sync</c> FEATURE was ON, so the Settings page said it was
/// running and the Jobs page rendered it as an ordinary healthy job. §640 then built
/// <see cref="SponsorZohoReconcileJob"/> over the path that actually works, at the §542 cadence he
/// asked for. Keeping a second, dormant sponsor sync listed would leave the §637 trap armed for
/// whoever next set that config key.</para>
///
/// <para>🔒 <b>Two sponsor syncs writing the same records is DATA LOSS, not inefficiency</b> — Zoho
/// hard-caps contact-e-mail updates at 3, and a burnt cap means *"a disaster as leads will be
/// lost"* (§596.1). Retiring this job removes that possibility at the root rather than relying on
/// a switch staying off. <see cref="SponsorZohoReconcileJob"/>'s interlock is kept as a tripwire in
/// case anyone ever re-adds the attribute below.</para>
///
/// <para><b>The class is kept, not deleted</b> — same treatment as §244/§252 F2. The code documents
/// what the legacy sync did, and <see cref="BackstageSyncService"/> remains reachable for a manual
/// investigation; nothing schedules it.</para>
///
/// <para>Historic behaviour, for reference: derived the sponsor/exhibitor list from completed
/// WooCommerce orders, then ran <see cref="BackstageSyncService"/> — each exhibitor checked against
/// Backstage and created if missing, with the event coordinator e-mailed. In TESTMODE only the
/// configured test sponsor was examined.</para>
/// </remarks>
public sealed class BackstageSyncJob
{
    private readonly WooCommerceClient _woo;
    private readonly BackstageSyncService _sync;
    private readonly BackstageSyncOptions _options;
    private readonly TestModeOptions _testMode;
    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly ILogger<BackstageSyncJob> _log;

    public BackstageSyncJob(
        WooCommerceClient woo,
        BackstageSyncService sync,
        BackstageSyncOptions options,
        TestModeOptions testMode,
        CommunityHubDbContext db,
        FeatureGateService gate,
        IAuditTrail audit,
        ILogger<BackstageSyncJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _woo = woo;
        _sync = sync;
        _options = options;
        _testMode = testMode;
        _db = db;
        _gate = gate;
        _audit = audit;
        _log = log;
        _activity = activity;
    }

    // §545(b) — optional, so an un-instrumented job simply says nothing (silence = UNKNOWN).
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    /// <summary>Daily at 06:30 UTC - after the WooCommerce pull (06:00).</summary>
    // 🔒 §641: NO [Function]/[TimerTrigger] attribute — the job is RETIRED and must never be
    // scheduled. (Was: base tick "0 */5 * * * *", §510 interval 10 minutes.) Re-adding the
    // attribute would put a SECOND sponsor sync alongside SponsorZohoReconcileJob and put Zoho's
    // 3-attempt contact-e-mail cap back at risk — read §640.1 before you do.
    public async Task Run(
        TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("BackstageSyncJob: disabled by config.");
            _activity?.ReportInactive(
                "Backstage sync is switched off in config, so no exhibitor reaches Zoho.");
            return;
        }

        // GATE (REQUIREMENTS §23): the Backstage/Zoho sync is an advanced feature,
        // off by default. This job is fleet-wide (not edition-scoped), so it runs
        // only while at least one active edition has 'backstage-sync' enabled; when
        // every active edition has it off the job no-ops (no Backstage calls / sends).
        var activeEventIds = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(ct);
        var anyEnabled = false;
        foreach (var id in activeEventIds)
        {
            if (await _gate.IsFeatureEnabledAsync("backstage-sync", id, ct))
            {
                anyEnabled = true;
                break;
            }
        }
        if (!anyEnabled)
        {
            _log.LogInformation(
                "BackstageSyncJob: feature 'backstage-sync' disabled for all active editions, skipped.");
            _activity?.ReportInactive(
                "The 'backstage-sync' feature is off for EVERY active edition, so no exhibitor is "
                + "being synced to Zoho.");
            return;
        }

        _activity?.ReportWork();

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

        // Named Engine event (REQUIREMENTS §24) — only when the sync created/flagged/
        // failed something (a no-change daily run isn't worth a trail row).
        if (result.Created + result.WouldCreate + result.Failed > 0)
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = activeEventIds.FirstOrDefault(),
                Category = AuditCategory.Engine,
                Action = "backstage-sync",
                ActorEmail = "system",
                Source = AuditSource.Job,
                Outcome = result.Failed > 0 ? AuditOutcome.Failure : AuditOutcome.Success,
                Summary = $"Backstage sync: {result.Examined} examined, {result.Created} created, "
                    + $"{result.WouldCreate} flagged, {result.Failed} failed",
            }, ct);
    }
}
