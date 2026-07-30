using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// The §38e/§58 SPEAKER CHANGE DETECTION timer job — the speaker analogue of
/// <see cref="SessionChangeDetectionJob"/>. Hourly (offset to :50 so it doesn't collide with
/// the session job at :40), it asks the <see cref="SpeakerChangeDetectionService"/> to pull
/// the current Zoho Backstage speakers, diff each LINKED CEH speaker's
/// name/tagline/bio/country/social against the CEH-stored snapshot, and ENQUEUE a real change
/// to the §59 approval queue (first-populate seeds silently; never auto-applies, never
/// deletes). Gated: Zoho enabled, an active edition, the <c>speaker-change-alerts</c> feature
/// enabled, and the edition's SPEAKER sync direction at stage 3 (Zoho→CEH) — else the service
/// returns Inactive and the job no-ops. The speakers API is inert until the
/// <c>ZohoBackstage.speaker.READ</c> scope + Zoho:SpeakerReadEnabled are set, so the service
/// no-ops gracefully and the job logs the unavailable reason until the source is wired.
/// </summary>
public sealed class SpeakerChangeDetectionJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoOptions _options;
    private readonly FeatureGateService _gate;
    private readonly SpeakerChangeDetectionService _service;
    private readonly ILogger<SpeakerChangeDetectionJob> _log;
    // §545(b) — optional, so a job can be instrumented without touching its wiring and an
    // un-instrumented job simply says nothing (silence = UNKNOWN, never flagged).
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public SpeakerChangeDetectionJob(
        CommunityHubDbContext db, ZohoOptions options, FeatureGateService gate,
        SpeakerChangeDetectionService service, ILogger<SpeakerChangeDetectionJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db; _options = options; _gate = gate; _service = service; _log = log;
        _activity = activity;
    }

    [Function("SpeakerChangeDetectionJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("SpeakerChangeDetectionJob: Zoho disabled.");
            _activity?.ReportInactive("Zoho is switched off, so nothing is compared.");
            return;
        }

        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SpeakerChangeDetectionJob: no active event."); return; }

        if (!await _gate.IsFeatureEnabledAsync(SpeakerChangeDetectionService.FeatureKey, eventId.Value, ct))
        {
            _log.LogInformation("SpeakerChangeDetectionJob: feature off.");
            _activity?.ReportInactive(
                $"The '{SpeakerChangeDetectionService.FeatureKey}' feature is switched off, so speaker "
                + "changes made in Zoho are not being detected.");
            return;
        }

        var result = await _service.RunAsync(eventId.Value, ct);

        // §58: gated on the edition's SPEAKER sync direction being stage 3 (Zoho→CEH). At the
        // default stage 1 (or stage 2) the service returns Inactive and we no-op here.
        if (result.DirectionInactive)
        {
            _log.LogInformation("SpeakerChangeDetectionJob: {Reason}.", result.UnavailableReason);
            _activity?.ReportInactive(
                result.UnavailableReason ?? "The edition's speaker sync direction excludes this engine.");
            return;
        }

        if (!result.SourceAvailable)
        {
            _log.LogWarning(
                "SpeakerChangeDetectionJob: source unavailable — {Reason}", result.UnavailableReason);
            // 🔒 §621 was EXACTLY this branch: Zoho:SpeakerReadEnabled was never set, so this
            // returned "unavailable" without ever calling Zoho — for as long as the feature existed.
            _activity?.ReportInactive(
                "The Zoho speaker list could not be read, so nothing was compared: "
                + (result.UnavailableReason ?? "reason not given") + ".");
            return;
        }

        _activity?.ReportWork();

        // §59: a real change is ENQUEUED to the delta-approval queue (not auto-applied);
        // the operator approves/rejects it in /Organizer/SyncQueue.
        _log.LogInformation(
            "SpeakerChangeDetectionJob: matched {Matched} — seeded {Seeded}, changed {Changed}, enqueued {Enqueued}, unmatched {Unmatched}.",
            result.Matched, result.Seeded, result.Changed, result.Enqueued, result.Unmatched);
    }
}
