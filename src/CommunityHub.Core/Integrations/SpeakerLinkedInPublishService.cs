using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// The outcome of a speaker-initiated LinkedIn publish request (REQUIREMENTS §52).
/// </summary>
/// <param name="Outcome">What happened — see <see cref="SpeakerLinkedInPublishOutcome"/>.</param>
/// <param name="Message">A human-readable, speaker-facing status line.</param>
/// <param name="PostId">The created <see cref="SoMePost"/> id (when one was queued), else null.</param>
/// <param name="ExternalPostId">The LinkedIn post id when it actually went live, else null.</param>
public sealed record SpeakerLinkedInPublishResult(
    SpeakerLinkedInPublishOutcome Outcome,
    string Message,
    int? PostId,
    string? ExternalPostId);

/// <summary>What a speaker LinkedIn publish attempt resulted in (REQUIREMENTS §52).</summary>
public enum SpeakerLinkedInPublishOutcome
{
    /// <summary>The post was actually posted to the LinkedIn company page (creds live, DryRun off).</summary>
    Published = 0,

    /// <summary>
    /// The post was added to the SoMe queue but NOT posted live, because the
    /// LinkedIn publish path is gated (no OAuth app/credentials, or the org-wide
    /// DryRun/disabled hold). It will go out automatically once the LinkedIn OAuth
    /// app is configured + posting is enabled — nothing is faked.
    /// </summary>
    QueuedAwaitingCredentials = 1,

    /// <summary>The speaker-publish feature is turned off for this edition (linkedin-queue flag).</summary>
    FeatureDisabled = 2,

    /// <summary>The graphic the speaker asked to publish was not found / not theirs / not released.</summary>
    GraphicNotAvailable = 3,

    /// <summary>The publish attempt errored; the queue post (if any) records the error.</summary>
    Failed = 4,
}

/// <summary>
/// Speaker self-service "Publish my session/master-class announcement to LinkedIn"
/// (REQUIREMENTS §52). A speaker can push their RELEASED announcement graphic + a
/// generated post text to the event's LinkedIn company page — through the SAME
/// gated publisher/queue path Content Studio and the SoMe dispatch job use
/// (<see cref="SoMeQueueService"/> → <see cref="SoMeDispatchService"/> →
/// <see cref="ILinkedInPostPublisher"/>), so behaviour, idempotency and the
/// secret-clean credential gate are identical and there is exactly one publish
/// code path.
///
/// <b>Nothing posts live until LinkedIn is wired.</b> Three independent holds, all
/// reused from §19, protect this:
///   1. the <c>linkedin-queue</c> feature flag must be on for the edition;
///   2. the SoMe settings must be Enabled with a configured company page;
///   3. the <see cref="ILinkedInPostPublisher"/> must be the live publisher AND
///      credentialed AND have <see cref="LinkedInOptions.DryRun"/> off.
/// When the publish path is gated, the speaker's announcement is QUEUED (a real
/// <see cref="SoMePost"/>, Active) and the speaker is told it will go out once the
/// LinkedIn OAuth app is configured — no token is invented and no post id is faked.
/// </summary>
public sealed class SpeakerLinkedInPublishService
{
    /// <summary>The feature flag that gates speaker LinkedIn publishing (shared with the §19 queue).</summary>
    public const string FeatureKey = "linkedin-queue";

    private readonly CommunityHubDbContext _db;
    private readonly SoMeQueueService _queue;
    private readonly SoMeDispatchService _dispatch;
    private readonly SoMeSettingsService _settings;
    private readonly GraphicsService _graphics;
    private readonly FeatureGateService _features;
    private readonly TimeProvider _clock;
    private readonly ILogger<SpeakerLinkedInPublishService>? _log;

    public SpeakerLinkedInPublishService(
        CommunityHubDbContext db,
        SoMeQueueService queue,
        SoMeDispatchService dispatch,
        SoMeSettingsService settings,
        GraphicsService graphics,
        FeatureGateService features,
        TimeProvider clock,
        ILogger<SpeakerLinkedInPublishService>? log = null)
    {
        _db = db;
        _queue = queue;
        _dispatch = dispatch;
        _settings = settings;
        _graphics = graphics;
        _features = features;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Whether the speaker LinkedIn-publish action should be OFFERED to a speaker
    /// for an edition. True when the feature flag is on AND the SoMe queue is
    /// configured (enabled + a company page). The action may still QUEUE rather
    /// than post-live when the publisher credentials are not wired yet — that is
    /// surfaced at publish time, not hidden here.
    /// </summary>
    public async Task<bool> IsOfferedAsync(int eventId, CancellationToken ct = default)
    {
        if (!await _features.IsFeatureEnabledAsync(FeatureKey, eventId, ct)) return false;
        var settings = await _settings.GetAsync(eventId, ct);
        return (settings?.Enabled ?? false)
               && !string.IsNullOrWhiteSpace(settings!.CompanyPageUrlOrOrgId);
    }

    /// <summary>
    /// Publish (or, when gated, queue) a speaker's announcement for one of THEIR
    /// OWN released graphics. Idempotent on the underlying SoMe post: it reuses an
    /// existing, still-Queued post for the same speaker+session rather than
    /// stacking duplicates, and never re-publishes one already Published.
    /// </summary>
    /// <param name="eventId">The edition.</param>
    /// <param name="participantId">The requesting speaker (ownership is enforced).</param>
    /// <param name="graphicAssetId">The released speaker/session graphic to announce.</param>
    /// <param name="speakerName">The speaker's display name (for the auto text).</param>
    /// <param name="byEmail">The speaker's email (audit stamp on the post).</param>
    public async Task<SpeakerLinkedInPublishResult> PublishAsync(
        int eventId,
        int participantId,
        int graphicAssetId,
        string speakerName,
        string? byEmail,
        CancellationToken ct = default)
    {
        if (!await _features.IsFeatureEnabledAsync(FeatureKey, eventId, ct))
        {
            return new SpeakerLinkedInPublishResult(
                SpeakerLinkedInPublishOutcome.FeatureDisabled,
                "LinkedIn publishing isn't enabled for this event yet.",
                null, null);
        }

        // Ownership + release gate: only the speaker's OWN, RELEASED, non-sponsor graphic.
        var graphic = await _db.GraphicAssets.FirstOrDefaultAsync(
            g => g.EventId == eventId
                 && g.Id == graphicAssetId
                 && g.ParticipantId == participantId
                 && g.Status == GraphicAssetStatus.Released
                 && g.Type != GraphicAssetType.Sponsor,
            ct);
        if (graphic is null)
        {
            return new SpeakerLinkedInPublishResult(
                SpeakerLinkedInPublishOutcome.GraphicNotAvailable,
                "That graphic isn't available to publish (it must be released to you first).",
                null, null);
        }

        var sessionId = graphic.SessionId;

        // Reuse an existing still-Queued post for the same speaker+session (idempotent),
        // else create a fresh auto-generated Speaker post scheduled to go out now.
        var existing = await _db.SoMePosts.FirstOrDefaultAsync(
            p => p.EventId == eventId
                 && p.Type == SoMePostType.Speaker
                 && p.ParticipantId == participantId
                 && p.SessionId == sessionId
                 && p.Status == SoMePostStatus.Queued,
            ct);

        SoMePost post;
        if (existing is not null)
        {
            existing.IsActive = true;
            if (existing.ScheduledAtUtc > _clock.GetUtcNow())
                existing.ScheduledAtUtc = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            post = existing;
        }
        else
        {
            post = await _queue.CreateSpeakerPostAsync(
                eventId, participantId, sessionId,
                scheduledAtUtc: _clock.GetUtcNow(),
                autoGenerate: true,
                byEmail: byEmail,
                ct);
        }

        // Attach the speaker's chosen released graphic when the auto path didn't
        // already pick one up (a manual/explicit image always wins — never clobbered).
        if (string.IsNullOrWhiteSpace(post.ImageRef)
            && !string.IsNullOrWhiteSpace(graphic.SharePointUrl ?? graphic.SharePointPath))
        {
            post.ImageRef = graphic.SharePointUrl ?? graphic.SharePointPath;
            await _db.SaveChangesAsync(ct);
        }

        // Run the SAME dispatch path Content Studio / the SoMe job use. All three
        // credential holds (settings.Enabled + page, publisher.CanPublish, DryRun)
        // live there — when gated the post is left Queued and nothing is posted.
        SoMeDispatchResult dispatch;
        try
        {
            dispatch = await _dispatch.DispatchDueAsync(eventId, ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex,
                "Speaker LinkedIn publish: dispatch threw for event {EventId} post {PostId}.",
                eventId, post.Id);
            return new SpeakerLinkedInPublishResult(
                SpeakerLinkedInPublishOutcome.Failed,
                "Something went wrong publishing to LinkedIn. Your announcement is queued and will be retried.",
                post.Id, null);
        }

        // Re-read the post to learn its real fate (the dispatcher mutated it).
        await _db.Entry(post).ReloadAsync(ct);

        if (post.Status == SoMePostStatus.Published)
        {
            return new SpeakerLinkedInPublishResult(
                SpeakerLinkedInPublishOutcome.Published,
                "Your announcement was published to the event's LinkedIn page.",
                post.Id, post.ExternalPostId);
        }

        if (post.Status == SoMePostStatus.Failed)
        {
            return new SpeakerLinkedInPublishResult(
                SpeakerLinkedInPublishOutcome.Failed,
                "LinkedIn publishing failed: " + (post.LastError ?? "unknown error")
                    + ". Your announcement stays queued and will be retried.",
                post.Id, null);
        }

        // Still Queued => the publish path is gated (no LinkedIn OAuth app/creds yet,
        // or the org-wide DryRun/disabled hold). Honest, no faking.
        _log?.LogInformation(
            "Speaker LinkedIn publish: post {PostId} for event {EventId} queued (publish gated): {DispatchMsg}",
            post.Id, eventId, dispatch.Message);
        return new SpeakerLinkedInPublishResult(
            SpeakerLinkedInPublishOutcome.QueuedAwaitingCredentials,
            "Your announcement is queued. It will be posted to the event's LinkedIn page automatically "
                + "once LinkedIn publishing is fully switched on by the organizers — nothing is posted before then.",
            post.Id, null);
    }
}
