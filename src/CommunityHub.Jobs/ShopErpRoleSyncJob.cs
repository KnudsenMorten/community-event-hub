using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1305 item 8 — keeps CEH-registered companies' signer / event coordinator in step with e-conomic
/// (<see cref="ShopErpRoleSyncService"/>) — §1312: every company with an e-conomic number. Every 10 minutes; gated by <c>sponsor-webshop</c>; reads e-conomic only.
/// </summary>
public sealed class ShopErpRoleSyncJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ShopErpRoleSyncService _sync;
    private readonly FeatureGateService _gate;
    private readonly ILogger<ShopErpRoleSyncJob> _log;

    public ShopErpRoleSyncJob(CommunityHubDbContext db, ShopErpRoleSyncService sync, FeatureGateService gate, ILogger<ShopErpRoleSyncJob> log)
    {
        _db = db;
        _sync = sync;
        _gate = gate;
        _log = log;
    }

    // BASE TICK ONLY — the real cadence (30 min) is the JobCatalog interval.
    [Function("ShopErpRoleSyncJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        foreach (var eventId in await _db.Events.Where(e => e.IsActive).Select(e => e.Id).ToListAsync(ct))
        {
            if (!await _gate.IsFeatureEnabledAsync(ShopMailer.FeatureKey, eventId, ct)) continue;
            var r = await _sync.RunAsync(eventId, ct);
            foreach (var p in r.Problems) _log.LogWarning("§1305 roles from e-conomic: {Problem}", p);
        }
    }
}
