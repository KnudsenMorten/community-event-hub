using CommunityHub.Core.Data;
using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §640 — the SCHEDULED sponsor/exhibitor reconcile to Zoho Backstage. Operator decision,
/// 2026-07-29: *"add the timer over SponsorZohoSyncService"*.
/// </summary>
/// <remarks>
/// <para><b>Why this job exists.</b> §637 found that PROD had <b>no scheduled sponsor/exhibitor
/// sync at all</b>: <see cref="BackstageSyncJob"/> is disabled by a config switch that has never
/// been set, and <see cref="SponsorZohoSyncService"/> — the path that actually works — was injected
/// only by web pages. Sponsor data therefore reached Zoho <i>only when a human saved a form</i>. A
/// description edited in Company Manager, a coordinator changed in CM (§626), or a record deleted
/// in Zoho (§632) all sat unnoticed until someone happened to open a page.</para>
///
/// <para><b>Why THIS service rather than enabling the other job.</b> It is the path already proven
/// in production — §596 (descriptions), §626 (coordinator), §632 (stale-link self-heal) all run
/// through it — and it does <b>not</b> send the missing-exhibitor coordinator mail that §542 weighed
/// at 144×/day. <c>MigrateCoordinatorsAndResyncAsync</c> is documented "re-runnable", takes ONE
/// Zoho token for the whole pass (avoiding per-company rate limits), and emits ONE batched ops mail
/// rather than one per company (§302's "70-mail night").</para>
///
/// <para>🔒 <b>THE INTERLOCK BELOW IS THE IMPORTANT PART.</b> Two sponsor syncs writing the same
/// records would double every write — and Zoho <b>hard-caps contact-e-mail updates at 3</b>, whose
/// failure mode in his words is *"the sponsor object and exhibitor goes into a stale state … which
/// is a disaster as leads will be lost"*. So if the legacy <see cref="BackstageSyncJob"/> is ever
/// switched on, THIS job stands down rather than racing it.</para>
///
/// <para>DEV cannot write: every sponsor/exhibitor create and update in <see cref="ZohoClient"/>
/// passes <c>MayWriteAsync</c>, and §612 made <c>Integrations:AllowExternalWrites</c> a ceiling.</para>
/// </remarks>
public sealed class SponsorZohoReconcileJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SponsorZohoSyncService _sync;
    private readonly ZohoOptions _zoho;
    private readonly BackstageSyncOptions _legacy;
    private readonly FeatureGateService _gate;
    private readonly ILogger<SponsorZohoReconcileJob> _log;
    private readonly JobActivityReporter? _activity;

    /// <summary>The feature switch this job obeys — the same one the legacy job used.</summary>
    public const string FeatureKey = "backstage-sync";

    public SponsorZohoReconcileJob(
        CommunityHubDbContext db,
        SponsorZohoSyncService sync,
        ZohoOptions zoho,
        BackstageSyncOptions legacy,
        FeatureGateService gate,
        ILogger<SponsorZohoReconcileJob> log,
        JobActivityReporter? activity = null)
    {
        _db = db; _sync = sync; _zoho = zoho; _legacy = legacy;
        _gate = gate; _log = log; _activity = activity;
    }

    [Function("SponsorZohoReconcileJob")]
    // §510 — a BASE TICK, not the cadence. The real frequency is the operator-editable
    // JobRunState.MinIntervalMinutes, defaulting to JobCatalog's 10 — which is the cadence he asked
    // for in §542 (*"it should run every 10 min"*), finally on a path that actually runs.
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (!_zoho.Enabled)
        {
            _log.LogInformation("SponsorZohoReconcileJob: Zoho disabled.");
            _activity?.ReportInactive("Zoho is switched off, so no sponsor or exhibitor is synced.");
            return;
        }

        // 🔒 INTERLOCK — never let two sponsor syncs write the same records. Zoho caps contact-email
        // updates at 3 and a burnt cap loses an exhibitor's leads (§596.1), so racing is not a
        // performance problem, it is data loss. The LEGACY job wins if it is ever switched on,
        // because that is the deliberate act; this job is the default and stands down.
        if (_legacy.Enabled)
        {
            _log.LogWarning(
                "SponsorZohoReconcileJob: the legacy BackstageSyncJob is ENABLED, so this job stood "
                + "down to avoid double-writing sponsor records. Only one may run.");
            _activity?.ReportInactive(
                "The legacy 'BackstageSync' job is switched on in config, so this reconcile stands "
                + "down — two sponsor syncs writing the same records would burn Zoho's 3-attempt "
                + "contact-e-mail cap. Turn one of them off.");
            return;
        }

        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("SponsorZohoReconcileJob: no active edition.");
            _activity?.ReportInactive("There is no ACTIVE edition, so nothing is reconciled.");
            return;
        }

        if (!await _gate.IsFeatureEnabledAsync(FeatureKey, eventId.Value, ct))
        {
            _log.LogInformation("SponsorZohoReconcileJob: feature '{Key}' off.", FeatureKey);
            _activity?.ReportInactive(
                $"The '{FeatureKey}' feature is switched off, so sponsor and exhibitor changes are "
                + "not reaching Zoho.");
            return;
        }

        var r = await _sync.MigrateCoordinatorsAndResyncAsync(eventId.Value, ct);

        // §545 NO-OP STREAK — zero companies examined is abnormal for a live, sold event: it means
        // the reconcile ran but had nothing to look at, which is a data problem rather than a switch.
        _activity?.ReportExamined(r.Companies, "sponsor companies to reconcile");

        _log.LogInformation(
            "SponsorZohoReconcileJob: {Companies} company(ies) — coordinators filled {Filled}, "
            + "sponsors synced {Sponsors}, exhibitors synced {Exhibitors}, failed {Failed}.",
            r.Companies, r.CoordinatorsFilled, r.SponsorsSynced, r.ExhibitorsSynced, r.Failed);

        foreach (var note in r.Notes)
            _log.LogWarning("SponsorZohoReconcileJob: {Note}", note);
    }
}
