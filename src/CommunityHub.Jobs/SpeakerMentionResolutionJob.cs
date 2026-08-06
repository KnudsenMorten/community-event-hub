using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §858.16c — fills each speaker's cached LinkedIn person URN so posts can <b>tag</b> them.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This job is the ONLY caller of LinkedIn's people lookup.</b> The endpoint carries a
/// DAY throttle and a member URN never changes, so resolution belongs in a scheduled routine and
/// never in the request or publish path — <c>{Speakers}</c> reads the cache only.</para>
///
/// <para>🔑 <b>Re-running is the point, not waste.</b> Anyone already resolved is skipped without a
/// call; only the unresolved are asked again. LinkedIn will mention a member only while they FOLLOW
/// the page (§858.16d), so a speaker who follows tomorrow becomes taggable on the next run with
/// nobody doing anything. Measured on the real roster: ~16 of 22 resolve, and the rest are named in
/// plain text with a report saying who.</para>
///
/// <para>⚠️ <b>Every 30 minutes</b> (operator 2026-08-06), so a newly added speaker or sponsor contact
/// is tagged within the hour rather than tomorrow. 🔒 That cadence is only affordable because of the
/// skip rules in <see cref="SpeakerMentionResolutionService.ShouldSkip"/>: a resolved person is never
/// asked about again, and a negative answer is trusted for 24h. Without them this would spend ~1,500
/// calls a day on a DAY-limited endpoint and every result would become LookupFailed.</para>
/// </remarks>
public sealed class SpeakerMentionResolutionJob
{
    /// <summary>Gated with the LinkedIn queue: if we cannot post, resolving who to tag is pointless.</summary>
    public const string FeatureKey = "linkedin-queue";

    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly SpeakerMentionResolutionService _resolver;
    private readonly ILogger<SpeakerMentionResolutionJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public SpeakerMentionResolutionJob(
        CommunityHubDbContext db,
        FeatureGateService gate,
        SpeakerMentionResolutionService resolver,
        ILogger<SpeakerMentionResolutionJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db; _gate = gate; _resolver = resolver; _log = log; _activity = activity;
    }

    // Base tick only (§510/§869.3) — the operator's interval on the Jobs page is the real cadence.
    [Function("SpeakerMentionResolutionJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("SpeakerMentionResolutionJob: no active event.");
            return;
        }

        if (!await _gate.AreAllEnabledAsync(eventId.Value, ct, FeatureKey))
        {
            _activity?.ReportInactive(
                "LinkedIn posting is switched off, so speaker mentions are not being resolved.");
            return;
        }

        var org = await _db.SoMeSettings
            .Where(s => s.EventId == eventId.Value)
            .Select(s => s.CompanyPageUrlOrOrgId)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(org))
        {
            // 🔒 Say WHICH thing is missing. "No company page is configured" is actionable;
            // a silent no-op is the §854 defect.
            _activity?.ReportInactive(
                "No LinkedIn company page is configured, so no speaker could be resolved to a mention.");
            return;
        }

        var result = await _resolver.ResolveAsync(eventId.Value, org!, reresolveAll: false, ct);

        // ⚠️ §858.16h — a sweep that could not ASK is not a sweep that found nothing. Report the two
        // differently, or a throttled night reads on the Jobs page as "nobody follows the page".
        if (!result.IsTrustworthy)
        {
            _log.LogWarning(
                "Speaker mention resolution completed with FAILED lookups — {Result}", result);
            _activity?.ReportInactive(
                $"{result.Failed} lookup(s) FAILED (throttle or connection), so the counts are not a "
                + $"measurement of who follows the page. Resolved {result.Resolved} this run; "
                + "the rest will be retried.");
            return;
        }

        if (result.Resolved == 0)
        {
            _activity?.ReportInactive(
                result.Skipped == result.Considered
                    ? $"All {result.Considered} speaker(s) already have a mention URN — nothing to do."
                    : $"No new speaker could be mentioned: {result.NotFollowers} do not follow the page"
                      + (result.Ambiguous > 0 ? $", {result.Ambiguous} were ambiguous" : string.Empty)
                      + ".");
            return;
        }

        _log.LogInformation("Speaker mention resolution — {Result}", result);
    }
}
