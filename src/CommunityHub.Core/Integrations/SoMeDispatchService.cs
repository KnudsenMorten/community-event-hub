using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>Outcome of one SoMe dispatch run for an edition (surfaced in the job log).</summary>
/// <param name="Published">Posts actually published this run.</param>
/// <param name="Failed">Posts whose publish attempt failed (error recorded, not dropped).</param>
/// <param name="Skipped">Due posts skipped because the publisher is not configured / posting disabled.</param>
/// <param name="PreAlertsSent">T-5-minute speaker pre-alert emails sent this run.</param>
/// <param name="Message">Human-readable status.</param>
public sealed record SoMeDispatchResult(
    int Published,
    int Failed,
    int Skipped,
    int PreAlertsSent,
    string Message);

/// <summary>
/// The publish half of the LinkedIn company-page SoMe queue (REQUIREMENTS §19) —
/// the "social media calendar" dispatcher. Run on a schedule (and on demand): it
/// publishes every DUE, <see cref="SoMePost.IsActive"/>,
/// <see cref="SoMePostStatus.Queued"/> post through the gated
/// <see cref="ILinkedInPostPublisher"/> seam, and sends the T-5-minute speaker
/// pre-alert.
///
/// <b>Idempotent (never double-post):</b> the post's own status is the sent-marker
/// — a published post flips to <see cref="SoMePostStatus.Published"/> with an
/// <see cref="SoMePost.ExternalPostId"/>, so a re-run never re-publishes it
/// (mirrors the <c>SentReminder</c> ledger contract). A failure flips to
/// <see cref="SoMePostStatus.Failed"/> + records <see cref="SoMePost.LastError"/>
/// — never silently dropped.
///
/// <b>Gated:</b> when SoMe posting is disabled OR the publisher is the Null no-op
/// (<see cref="ILinkedInPostPublisher.CanPublish"/> = false) OR no company page is
/// configured, due posts are SKIPPED + left Queued — nothing is faked.
/// </summary>
public sealed class SoMeDispatchService
{
    /// <summary>How far before a Speaker post the pre-alert email fires.</summary>
    public static readonly TimeSpan PreAlertLeadTime = TimeSpan.FromMinutes(5);

    /// <summary>EmailLog / SentReminder category for the publish notification.</summary>
    public const string NotifyCategory = "some-published";

    /// <summary>EmailLog / SentReminder category for the speaker pre-alert.</summary>
    public const string PreAlertCategory = "some-speaker-prealert";

    private readonly CommunityHubDbContext _db;
    private readonly ILinkedInPostPublisher _publisher;
    private readonly SoMeSettingsService _settings;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor? _emailContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<SoMeDispatchService>? _log;

    public SoMeDispatchService(
        CommunityHubDbContext db,
        ILinkedInPostPublisher publisher,
        SoMeSettingsService settings,
        IEmailSender email,
        TimeProvider clock,
        IEmailContextAccessor? emailContext = null,
        ILogger<SoMeDispatchService>? log = null)
    {
        _db = db;
        _publisher = publisher;
        _settings = settings;
        _email = email;
        _clock = clock;
        _emailContext = emailContext;
        _log = log;
    }

    /// <summary>
    /// Run one dispatch pass for an edition: send T-5 pre-alerts for soon-due
    /// Speaker posts, then publish every due Active Queued post.
    /// </summary>
    public async Task<SoMeDispatchResult> DispatchDueAsync(
        int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var settings = await _settings.GetAsync(eventId, ct);

        // ----- T-5-minute speaker pre-alerts (independent of the posting gate;
        // the organizer needs the heads-up even before posting is enabled) -----
        var preAlerts = await SendSpeakerPreAlertsAsync(eventId, settings, now, ct);

        // ----- publish due posts -----
        var due = await _db.SoMePosts
            .Where(p => p.EventId == eventId
                        && p.IsActive
                        && p.Status == SoMePostStatus.Queued
                        && p.ScheduledAtUtc <= now)
            .OrderBy(p => p.ScheduledAtUtc)
            .ToListAsync(ct);

        if (due.Count == 0)
        {
            return new SoMeDispatchResult(0, 0, 0, preAlerts,
                "No due posts." + (preAlerts > 0 ? $" {preAlerts} pre-alert(s) sent." : string.Empty));
        }

        // Posting gate: settings.Enabled + a configured page + a wired publisher.
        var page = settings?.CompanyPageUrlOrOrgId;
        var canPost = (settings?.Enabled ?? false)
                      && !string.IsNullOrWhiteSpace(page)
                      && _publisher.CanPublish;

        if (!canPost)
        {
            _log?.LogInformation(
                "SoMeDispatch: {Count} due post(s) for event {EventId} skipped — "
                + "posting not configured (enabled={Enabled}, page set={PageSet}, publisher={Publisher}).",
                due.Count, eventId, settings?.Enabled ?? false,
                !string.IsNullOrWhiteSpace(page), _publisher.CanPublish);
            return new SoMeDispatchResult(0, 0, due.Count, preAlerts,
                "SoMe posting is not configured (disabled, no company page, or no wired "
                + $"LinkedIn publisher); {due.Count} due post(s) left Queued, nothing faked.");
        }

        int published = 0, failed = 0;
        foreach (var post in due)
        {
            try
            {
                var result = await _publisher.PublishAsync(
                    new LinkedInPost(page!, post.EffectiveText, post.ImageRef, post.TagList), ct);

                if (result.Published)
                {
                    post.Status = SoMePostStatus.Published;
                    post.PublishedAtUtc = _clock.GetUtcNow();
                    post.ExternalPostId = result.ExternalPostId;
                    post.LastError = null;
                    published++;
                    await _db.SaveChangesAsync(ct);
                    await NotifyPublishedAsync(eventId, settings!, post, ct);
                }
                else
                {
                    // Publisher declined (not configured at the seam) — leave Queued.
                    _log?.LogInformation(
                        "SoMeDispatch: post {PostId} not published: {Message}", post.Id, result.Message);
                }
            }
            catch (Exception ex)
            {
                post.Status = SoMePostStatus.Failed;
                post.LastError = Truncate(ex.Message, 2000);
                failed++;
                await _db.SaveChangesAsync(ct);
                _log?.LogWarning(ex, "SoMeDispatch: post {PostId} failed to publish.", post.Id);
            }
        }

        var msg = $"Dispatch complete: {published} published, {failed} failed, {preAlerts} pre-alert(s) sent.";
        _log?.LogInformation("SoMeDispatch: event {EventId} — {Message}", eventId, msg);
        return new SoMeDispatchResult(published, failed, 0, preAlerts, msg);
    }

    /// <summary>
    /// Email the designated pre-alert organizer 5 minutes before each Active
    /// Queued Speaker post publishes, so they can manually insert the speaker's
    /// real LinkedIn handle (the API can't tag external speakers). Idempotent via
    /// <see cref="SoMePost.SpeakerPreAlertSent"/>.
    /// </summary>
    private async Task<int> SendSpeakerPreAlertsAsync(
        int eventId, SoMeSettings? settings, DateTimeOffset now, CancellationToken ct)
    {
        var organizer = settings?.SpeakerPreAlertOrganizerEmail;
        if (string.IsNullOrWhiteSpace(organizer)) return 0;  // no designated organizer => no pre-alert

        var window = now.Add(PreAlertLeadTime);
        var soon = await _db.SoMePosts
            .Where(p => p.EventId == eventId
                        && p.Type == SoMePostType.Speaker
                        && p.IsActive
                        && p.Status == SoMePostStatus.Queued
                        && !p.SpeakerPreAlertSent
                        && p.ScheduledAtUtc <= window
                        && p.ScheduledAtUtc > now)
            .ToListAsync(ct);

        int sent = 0;
        foreach (var post in soon)
        {
            var subject = "[SoMe] Speaker post publishes in ~5 min — insert the LinkedIn handle";
            var body =
                "<p>A scheduled LinkedIn company-page post for a speaker is about to publish "
                + $"(at {post.ScheduledAtUtc:u}).</p>"
                + "<p>The LinkedIn API cannot tag an external speaker, so please open the "
                + "post and manually add the speaker's real LinkedIn handle now.</p>"
                + $"<p><strong>Post text:</strong><br/>{System.Net.WebUtility.HtmlEncode(post.EffectiveText)}</p>";

            try
            {
                using (_emailContext?.Set(new EmailContext(PreAlertCategory, eventId)))
                {
                    await _email.SendAsync(organizer!, subject, body, ct);
                }
                post.SpeakerPreAlertSent = true;
                await _db.SaveChangesAsync(ct);
                sent++;
            }
            catch (Exception ex)
            {
                // Don't mark sent => retried next run. Don't abort the batch.
                _log?.LogWarning(ex, "SoMeDispatch: pre-alert for post {PostId} failed.", post.Id);
            }
        }
        return sent;
    }

    /// <summary>
    /// Email the SoMe notification array when a post publishes (REQUIREMENTS §19),
    /// gated by <see cref="SoMeSettings.NotifyOnPublish"/>. A failure here never
    /// un-publishes the post (the post is already live).
    /// </summary>
    private async Task NotifyPublishedAsync(
        int eventId, SoMeSettings settings, SoMePost post, CancellationToken ct)
    {
        if (!settings.NotifyOnPublish) return;
        var recipients = settings.NotificationEmailList;
        if (recipients.Count == 0) return;

        var subject = "[SoMe] A LinkedIn company-page post was published";
        var body =
            "<p>A scheduled LinkedIn company-page post has just been published.</p>"
            + $"<p><strong>Type:</strong> {post.Type}</p>"
            + $"<p><strong>Text:</strong><br/>{System.Net.WebUtility.HtmlEncode(post.EffectiveText)}</p>";

        foreach (var to in recipients)
        {
            try
            {
                using (_emailContext?.Set(new EmailContext(NotifyCategory, eventId)))
                {
                    await _email.SendAsync(to, subject, body, ct);
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex,
                    "SoMeDispatch: publish notification to {To} failed (post already live).", to);
            }
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
