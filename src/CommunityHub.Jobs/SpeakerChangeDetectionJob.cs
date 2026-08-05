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
/// returns Inactive and the job no-ops.
///
/// <para>🗑 <b>§754.5 — the claim that "the speakers API is inert until the
/// <c>ZohoBackstage.speaker.READ</c> scope is set" is DELETED and must not come back.</b> The Zoho
/// Backstage credentials carry every permission CEH needs, and <c>/speakers</c> is read ungated in
/// production by the push engines. The gates are the ones listed above.</para>
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
            // 🔒 §621 was EXACTLY this branch: a config flag nobody had set made this return
            // "unavailable" without ever calling Zoho — for as long as the feature existed.
            // 🗑 §754.5: that flag is DELETED. Reaching this branch now means a REAL failed call, so
            // the reason below is the API's own — never a config excuse.
            _activity?.ReportInactive(
                "The Zoho speaker list could not be read, so nothing was compared: "
                + (result.UnavailableReason ?? "reason not given") + ".");
            return;
        }

        _activity?.ReportWork();

        // §737.2: a real change is ENQUEUED to the delta queue and AUTO-APPLIED in the same
        // pass (Zoho owns these fields). The row lands in /Organizer/SyncQueue's recently-decided
        // list marked "(auto)", so {Enqueued} counts changes applied, NOT changes awaiting him.
        _log.LogInformation(
            "SpeakerChangeDetectionJob: matched {Matched} — seeded {Seeded}, changed {Changed}, enqueued {Enqueued}, unmatched {Unmatched}.",
            result.Matched, result.Seeded, result.Changed, result.Enqueued, result.Unmatched);
    }
}
