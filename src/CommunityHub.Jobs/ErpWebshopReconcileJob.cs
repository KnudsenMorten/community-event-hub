using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// Timer (every 30 min) that runs the ERP→webshop sponsor-contact reconcile — the C#
/// port of the legacy Sync-ERP-Contacts-to-Webshop.ps1. All work lives in
/// <see cref="ErpWebshopContactSyncService"/> in Core (the same engine the organizer
/// "Reconcile ERP → webshop" button calls), so the manual + scheduled paths are
/// identical. Idempotent: it only creates MISSING webshop users and sets defaults
/// that are empty, never overwriting a curated value, and alerts the organizer for
/// contacts missing a Signer/Event-Coordinator role.
///
/// Gating: the 'erp-webshop-reconcile' feature flag (off by default) across active
/// editions, AND the service's own <see cref="ErpWebshopContactSyncService.CanRun"/>
/// (e-conomic + Company Manager configured). It is also covered by the global
/// jobs-pause switch via the worker middleware.
/// </summary>
public sealed class ErpWebshopReconcileJob
{
    private readonly ErpWebshopContactSyncService _sync;
    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly ILogger<ErpWebshopReconcileJob> _log;

    public ErpWebshopReconcileJob(
        ErpWebshopContactSyncService sync,
        CommunityHubDbContext db,
        FeatureGateService gate,
        IAuditTrail audit,
        ILogger<ErpWebshopReconcileJob> log)
    {
        _sync = sync;
        _db = db;
        _gate = gate;
        _audit = audit;
        _log = log;
    }

    [Function("ErpWebshopReconcileJob")]
    public async Task Run(
        [TimerTrigger("0 */30 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        // Fleet-wide engine (not edition-scoped): run only while at least one active
        // edition has the feature on; no-op otherwise.
        var activeEventIds = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => e.Id)
            .ToListAsync(ct);
        var anyEnabled = false;
        foreach (var id in activeEventIds)
        {
            if (await _gate.IsFeatureEnabledAsync("erp-webshop-reconcile", id, ct))
            {
                anyEnabled = true;
                break;
            }
        }
        if (!anyEnabled)
        {
            _log.LogInformation(
                "ErpWebshopReconcileJob: feature 'erp-webshop-reconcile' disabled for all active editions, skipped.");
            return;
        }

        if (!_sync.CanRun)
        {
            _log.LogInformation("ErpWebshopReconcileJob: e-conomic / Company Manager not configured; skipped.");
            return;
        }

        var r = await _sync.SyncAsync(ct);
        _log.LogInformation(
            "ErpWebshopReconcileJob: {Customers} customers, {Users} user(s) created, {Defaults} default(s) set, {Alerts} alert(s).",
            r.Customers, r.UsersCreated, r.DefaultsSet, r.Alerts);

        // Only audit a run that did something (or raised alerts) — idle polls would flood the trail.
        if (r.UsersCreated > 0 || r.DefaultsSet > 0 || r.Alerts > 0)
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = activeEventIds.FirstOrDefault(),
                Category = AuditCategory.Engine,
                Action = "erp-webshop-reconcile",
                ActorEmail = "system",
                Source = AuditSource.Job,
                Outcome = AuditOutcome.Success,
                Summary = $"ERP→webshop reconcile: {r.Customers} customers, {r.UsersCreated} user(s) created, "
                    + $"{r.DefaultsSet} default(s) set, {r.Alerts} alert(s)."
                    + (r.Alerts > 0 ? " " + string.Join("; ", r.AlertNotes.Take(8)) : ""),
            }, ct);
    }
}
