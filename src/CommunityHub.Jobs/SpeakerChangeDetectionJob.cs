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

    public SpeakerChangeDetectionJob(
        CommunityHubDbContext db, ZohoOptions options, FeatureGateService gate,
        SpeakerChangeDetectionService service, ILogger<SpeakerChangeDetectionJob> log)
    {
        _db = db; _options = options; _gate = gate; _service = service; _log = log;
    }

    [Function("SpeakerChangeDetectionJob")]
    public async Task Run([TimerTrigger("0 50 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (!_options.Enabled) { _log.LogInformation("SpeakerChangeDetectionJob: Zoho disabled."); return; }

        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SpeakerChangeDetectionJob: no active event."); return; }

        if (!await _gate.IsFeatureEnabledAsync(SpeakerChangeDetectionService.FeatureKey, eventId.Value, ct))
        { _log.LogInformation("SpeakerChangeDetectionJob: feature off."); return; }

        var result = await _service.RunAsync(eventId.Value, ct);

        // §58: gated on the edition's SPEAKER sync direction being stage 3 (Zoho→CEH). At the
        // default stage 1 (or stage 2) the service returns Inactive and we no-op here.
        if (result.DirectionInactive)
        {
            _log.LogInformation("SpeakerChangeDetectionJob: {Reason}.", result.UnavailableReason);
            return;
        }

        if (!result.SourceAvailable)
        {
            _log.LogWarning(
                "SpeakerChangeDetectionJob: source unavailable — {Reason}", result.UnavailableReason);
            return;
        }

        // §59: a real change is ENQUEUED to the delta-approval queue (not auto-applied);
        // the operator approves/rejects it in /Organizer/SyncQueue.
        _log.LogInformation(
            "SpeakerChangeDetectionJob: matched {Matched} — seeded {Seeded}, changed {Changed}, enqueued {Enqueued}, unmatched {Unmatched}.",
            result.Matched, result.Seeded, result.Changed, result.Enqueued, result.Unmatched);
    }
}
