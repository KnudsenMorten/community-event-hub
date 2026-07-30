using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// The §38e SESSION CHANGE DETECTION timer job. Hourly, it asks the
/// <see cref="SessionChangeDetectionService"/> to pull the current Zoho Backstage agenda,
/// diff it against the CEH-stored time/location per matched session, and email the
/// affected speaker(s) on a real change (ring + date gated; first-populate seeds
/// silently). Gated like <see cref="AttendeeBackstageSyncJob"/>: Zoho must be enabled,
/// there must be an active edition, and the <c>session-change-alerts</c> feature must be
/// enabled for it. The Backstage agenda API is currently inert (needs the
/// <c>ZohoBackstage.agenda.READ</c> scope + Zoho:AgendaReadEnabled), so the service
/// no-ops gracefully and the job logs the unavailable reason until the source is wired.
/// </summary>
public sealed class SessionChangeDetectionJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoOptions _options;
    private readonly FeatureGateService _gate;
    private readonly SessionChangeDetectionService _service;
    private readonly ILogger<SessionChangeDetectionJob> _log;
    // §545(b) — optional, so a job can be instrumented without touching its wiring and an
    // un-instrumented job simply says nothing (silence = UNKNOWN, never flagged).
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public SessionChangeDetectionJob(
        CommunityHubDbContext db, ZohoOptions options, FeatureGateService gate,
        SessionChangeDetectionService service, ILogger<SessionChangeDetectionJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db; _options = options; _gate = gate; _service = service; _log = log;
        _activity = activity;
    }

    [Function("SessionChangeDetectionJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("SessionChangeDetectionJob: Zoho disabled.");
            _activity?.ReportInactive("Zoho is switched off, so nothing is compared.");
            return;
        }

        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SessionChangeDetectionJob: no active event."); return; }

        if (!await _gate.IsFeatureEnabledAsync(SessionChangeDetectionService.FeatureKey, eventId.Value, ct))
        {
            _log.LogInformation("SessionChangeDetectionJob: feature off.");
            _activity?.ReportInactive(
                $"The '{SessionChangeDetectionService.FeatureKey}' feature is switched off, so session "
                + "changes made in Zoho are not being detected.");
            return;
        }

        var result = await _service.RunAsync(eventId.Value, ct);

        // §57: the engine is gated on the edition's sync direction being stage 3 (Zoho→CEH).
        // At the default stage 1 (or stage 2) the service returns Inactive and we no-op here.
        if (result.DirectionInactive)
        {
            _log.LogInformation(
                "SessionChangeDetectionJob: {Reason}.", result.UnavailableReason);
            // 🔒 §576 was EXACTLY this branch: both engines demanded a sync stage that had been
            // DELETED, so they no-opped every 5 minutes for months behind a green Jobs page.
            _activity?.ReportInactive(
                result.UnavailableReason ?? "The edition's session sync direction excludes this engine.");
            return;
        }

        if (!result.SourceAvailable)
        {
            _log.LogWarning(
                "SessionChangeDetectionJob: source unavailable — {Reason}", result.UnavailableReason);
            _activity?.ReportInactive(
                "The Zoho agenda could not be read, so nothing was compared: "
                + (result.UnavailableReason ?? "reason not given") + ".");
            return;
        }

        _activity?.ReportWork();

        // §59: a real change is ENQUEUED to the delta-approval queue (not auto-applied/emailed
        // inline); the operator approves/rejects it in /Organizer/SyncQueue.
        _log.LogInformation(
            "SessionChangeDetectionJob: matched {Matched} — seeded {Seeded}, changed {Changed}, enqueued {Enqueued}, unmatched {Unmatched}.",
            result.Matched, result.Seeded, result.Changed, result.Enqueued, result.Unmatched);
    }
}
