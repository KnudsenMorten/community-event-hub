using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
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
    /// <summary>The stable job key for the consecutive-failure marker + alert throttle.</summary>
    private const string JobKey = "erp-webshop-reconcile";

    private readonly ErpWebshopContactSyncService _sync;
    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly JobFailureTracker _failures;
    private readonly EngineAlertSender _alerts;
    private readonly ILogger<ErpWebshopReconcileJob> _log;

    public ErpWebshopReconcileJob(
        ErpWebshopContactSyncService sync,
        CommunityHubDbContext db,
        FeatureGateService gate,
        IAuditTrail audit,
        JobFailureTracker failures,
        EngineAlertSender alerts,
        ILogger<ErpWebshopReconcileJob> log)
    {
        _sync = sync;
        _db = db;
        _gate = gate;
        _audit = audit;
        _failures = failures;
        _alerts = alerts;
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

        // RUN with the "alert only on 2 CONSECUTIVE failures" gate (operator 2026-06-27,
        // the 503 incident: "a single failure is likely a backup/platform glitch — only
        // alert if it fails twice in a row"). A whole-reconcile crash here (e.g. the
        // initial ListCustomers/ListCompanies call) is caught, RECORDED for observability
        // (log + Failure audit), and the consecutive-failure counter is bumped; the
        // operator is paged only once the counter reaches 2. We deliberately do NOT
        // re-throw — re-throwing would trip EngineErrorAlertMiddleware and page on the
        // FIRST failure, defeating the gate. The marker survives restarts, so two genuine
        // back-to-back failures still alert across a redeploy. (Per-company failures are
        // already absorbed inside SyncAsync and do NOT count as a job failure.)
        ErpWebshopContactSyncService.SyncResult r;
        try
        {
            r = await _sync.SyncAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host shutdown — not a job failure; let the platform see the cancel.
        }
        catch (Exception ex)
        {
            var decision = await _failures.RecordFailureAsync(JobKey, ex.Message, ct);
            _log.LogError(ex,
                "ErpWebshopReconcileJob: run FAILED (consecutive failure #{N}); alert {Alert}.",
                decision.ConsecutiveFailures, decision.ShouldAlert ? "RAISED" : "suppressed");

            // Always record the failure for observability, even when the alert is suppressed.
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = activeEventIds.FirstOrDefault(),
                Category = AuditCategory.Engine,
                Action = "erp-webshop-reconcile",
                ActorEmail = "system",
                Source = AuditSource.Job,
                Outcome = AuditOutcome.Failure,
                Summary = $"ERP→webshop reconcile FAILED (consecutive #{decision.ConsecutiveFailures}): {ex.Message}"
                    + (decision.ShouldAlert ? " — operator alerted." : " — single failure, alert suppressed (likely transient)."),
            }, ct);

            if (decision.ShouldAlert)
            {
                var html =
                    $"<p>The ERP→webshop reconcile engine has now FAILED <b>{decision.ConsecutiveFailures}</b> "
                    + "times in a row, so this is no longer a one-off backup/platform glitch.</p>"
                    + $"<pre>{System.Net.WebUtility.HtmlEncode(ex.ToString())}</pre>";
                // Stable per-job throttle key so a job stuck failing every tick can't
                // flood the inbox (EngineAlertSender suppresses repeats within its window).
                await _alerts.AlertAsync(
                    "[ELDK27] Engine FAILED (2x in a row): ErpWebshopReconcileJob",
                    html, ct, throttleKey: $"engine-fail:{JobKey}");
            }
            return;
        }

        // Clean run — reset the consecutive-failure counter so the NEXT failure starts at 1.
        await _failures.RecordSuccessAsync(JobKey, ct);

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
