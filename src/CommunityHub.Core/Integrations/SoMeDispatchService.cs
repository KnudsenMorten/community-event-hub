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

    // §707.27 C1 — the organizer-inbox fallback for the pre-alert. Optional so every existing
    // construction (and every test) is unchanged; wired by DI at runtime.
    private readonly Microsoft.Extensions.Options.IOptions<EmailOptions>? _emailOptions;

    public SoMeDispatchService(
        CommunityHubDbContext db,
        ILinkedInPostPublisher publisher,
        SoMeSettingsService settings,
        IEmailSender email,
        TimeProvider clock,
        IEmailContextAccessor? emailContext = null,
        ILogger<SoMeDispatchService>? log = null,
        // §324: optional store — resolves a post's ImageRef graphic to RAW BYTES for the
        // native LinkedIn image upload. Absent (tests) ⇒ text-only, as before.
        Graphics.ISharePointFileStore? fileStore = null,
        Microsoft.Extensions.Options.IOptions<EmailOptions>? emailOptions = null)
    {
        _db = db;
        _publisher = publisher;
        _settings = settings;
        _email = email;
        _clock = clock;
        _emailContext = emailContext;
        _log = log;
        _fileStore = fileStore;
        _emailOptions = emailOptions;
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    /// <summary>
    /// §707.27 C3 — the edition name for the ops-mail subjects. Operator 2026-07-30: *"subject for
    /// these 2 should shown ELDK27 (eventname) instead of [SOME]"*.
    /// </summary>
    /// <remarks>
    /// Falls back to no prefix rather than to a hard-coded edition name: a subject that names the
    /// WRONG event is worse than one that names none, and this code is evergreen (§ guardrail —
    /// code is <c>CommunityHub</c>, never <c>eldk27</c>).
    /// </remarks>
    private async Task<string?> EventDisplayNameAsync(int eventId, CancellationToken ct) =>
        await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => e.DisplayName)
            .FirstOrDefaultAsync(ct);

    /// <summary>Prefix an ops subject with the edition name, or leave it bare when unknown.</summary>
    private static string WithEventPrefix(string? eventName, string subject) =>
        string.IsNullOrWhiteSpace(eventName) ? subject : $"{eventName}: {subject}";

    private readonly Graphics.ISharePointFileStore? _fileStore;

    /// <summary>
    /// §324: resolve a post's ImageRef (the graphic's SharePoint URL/path) to raw
    /// bytes via the GraphicAsset's stored item id. Fail-soft null (text-only post)
    /// when unresolvable — never blocks the publish on a missing image source.
    /// </summary>
    private async Task<(byte[]? Bytes, string? Alt)> ResolveImageAsync(
        SoMePost post, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(post.ImageRef) || _fileStore is null || !_fileStore.CanRead)
            return (null, null);
        try
        {
            var graphic = await _db.GraphicAssets.AsNoTracking().FirstOrDefaultAsync(
                g => g.EventId == post.EventId
                     && (g.SharePointUrl == post.ImageRef || g.SharePointPath == post.ImageRef),
                ct);
            if (graphic?.StorageItemId is not { Length: > 0 } itemId) return (null, null);
            var bytes = await _fileStore.DownloadAsync(itemId, ct);
            return (bytes is { Length: > 0 } ? bytes : null, "Session graphic");
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SoMeDispatch: image resolve failed for post {PostId}.", post.Id);
            return (null, null);
        }
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

        int published = 0, failed = 0, withdrawn = 0;
        foreach (var post in due)
        {
            try
            {
                // §329 — RE-VALIDATE THE SUBJECT IMMEDIATELY BEFORE PUBLISHING.
                //
                // A post is composed once and published later, sometimes weeks later. In
                // between, the speaker may withdraw, the session may be deleted or the
                // sponsor may pull out — and until now nothing re-checked: the queue would
                // happily announce someone who is no longer coming, publicly and
                // irreversibly. SoMePost.IsActive is DOCUMENTED for exactly this case ("a
                // speaker drops out, the organizer flips it off") but relied on a human
                // remembering, at the one moment nobody is watching: a bulk queue run.
                var reason = await RevalidateSubjectAsync(post, ct);
                if (reason is not null)
                {
                    // Deactivate rather than Fail: nothing failed, the subject went away.
                    // This also stops the post being re-picked on every later pass.
                    post.IsActive = false;
                    post.LastError = Truncate("Withdrawn before publishing: " + reason, 2000);
                    withdrawn++;
                    await _db.SaveChangesAsync(ct);
                    _log?.LogWarning(
                        "SoMeDispatch: post {PostId} NOT published and deactivated — {Reason}",
                        post.Id, reason);
                    continue;
                }

                // §324: attach the graphic's raw bytes for the native image upload.
                var (imageBytes, imageAlt) = await ResolveImageAsync(post, ct);
                var result = await _publisher.PublishAsync(
                    new LinkedInPost(page!, post.EffectiveText, post.ImageRef, post.TagList,
                        imageBytes, imageAlt), ct);

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

        var msg = $"Dispatch complete: {published} published, {failed} failed, "
                  + (withdrawn > 0 ? $"{withdrawn} withdrawn (subject gone), " : string.Empty)
                  + $"{preAlerts} pre-alert(s) sent.";
        _log?.LogInformation("SoMeDispatch: event {EventId} — {Message}", eventId, msg);
        return new SoMeDispatchResult(published, failed, 0, preAlerts, msg);
    }

    /// <summary>
    /// §329 — is this queued post's SUBJECT still real and still coming? Returns a
    /// human-readable reason when it is not, or null when the post is safe to publish.
    ///
    /// <para>Checked at publish time, not compose time, because that is the only moment
    /// that matters: a LinkedIn post can be deleted afterwards but never unsent, so
    /// announcing a withdrawn speaker is not recoverable by an organizer noticing later.</para>
    ///
    /// <para>Only the links the post actually carries are checked — a post with no
    /// participant, session or company (a plain announcement) is always valid. Each check
    /// is deliberately narrow: <b>absence</b> or an explicit <b>withdrawal</b>, never a
    /// heuristic, so a normal post is never silently suppressed.</para>
    /// </summary>
    private async Task<string?> RevalidateSubjectAsync(SoMePost post, CancellationToken ct)
    {
        if (post.ParticipantId is int pid)
        {
            var p = await _db.Participants.AsNoTracking()
                .Where(x => x.Id == pid)
                .Select(x => new { x.IsActive, x.FullName })
                .FirstOrDefaultAsync(ct);

            if (p is null) return $"participant {pid} no longer exists";
            if (!p.IsActive)
                return $"{(string.IsNullOrWhiteSpace(p.FullName) ? $"participant {pid}" : p.FullName)} "
                       + "is no longer active (withdrawn)";
        }

        if (post.SessionId is int sid)
        {
            var exists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sid, ct);
            if (!exists) return $"session {sid} no longer exists";
        }

        if (!string.IsNullOrWhiteSpace(post.SponsorCompanyId))
        {
            // §253 G8b: a withdrawn company stops being a sponsor — do not announce it.
            var company = await _db.SponsorInfos.AsNoTracking()
                .Where(s => s.EventId == post.EventId
                            && s.SponsorCompanyId == post.SponsorCompanyId)
                .Select(s => new { s.Status })
                .FirstOrDefaultAsync(ct);

            if (company is null) return $"sponsor company '{post.SponsorCompanyId}' no longer exists";
            if (company.Status != SponsorStatus.Active)
                return $"sponsor company '{post.SponsorCompanyId}' is {company.Status} (withdrawn)";
        }

        return null;
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
        // §707.27 C1 (operator 2026-07-30: *"some-speaker-prealert MUST GO TO info@expertslive.dk"*).
        // 🔒 The recipient is the edition's ORGANIZER INBOX, not one designated person. The per-edition
        // override remains for an edition that really does route this elsewhere, but an EMPTY override
        // no longer means "send nothing": this is a time-critical ops alert with a ~5 minute window, and
        // silently not sending it is the worst of the three outcomes.
        var organizer = FirstNonBlank(
            settings?.SpeakerPreAlertOrganizerEmail,
            _emailOptions?.Value.OrganizerInbox,
            _emailOptions?.Value.FromAddress);
        if (string.IsNullOrWhiteSpace(organizer)) return 0;  // nothing configured at all => no pre-alert

        var eventName = await EventDisplayNameAsync(eventId, ct);

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
            // §707.27 C3 — the EVENT NAME, not "[SoMe]". 🔒 Kept in step with
            // `EmailTemplateCatalog.InlineSubjects` in the same commit: the Settings page reads the
            // catalog, so changing one alone makes that row lie about what actually goes out.
            var subject = WithEventPrefix(
                eventName, "Speaker post publishes in ~5 min — insert the LinkedIn handle");
            var body =
                "<p>A scheduled LinkedIn company-page post for a speaker is about to publish "
                + $"(at {post.ScheduledAtUtc:u}).</p>"
                + "<p>The LinkedIn API cannot tag an external speaker, so please open the "
                + "post and manually add the speaker's real LinkedIn handle now.</p>"
                + $"<p><strong>Post text:</strong><br/>{System.Net.WebUtility.HtmlEncode(post.EffectiveText)}</p>";

            try
            {
                // §326ca — RING-EXEMPT, stated explicitly. Goes to ONE designated ORGANIZER
                // (SoMeSettings.SpeakerPreAlertOrganizerEmail), never a participant, and it
                // is time-critical: ~5 minutes to paste the speaker's handle before the post
                // goes live. An ops alert must not be silenceable by a participant rollout ring.
                using (_emailContext?.Set(new EmailContext(
                    PreAlertCategory, eventId, RingExempt: true)))
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

        // §707.27 C3 — the EVENT NAME, not "[SoMe]" (see the pre-alert above; the catalog's
        // InlineSubjects entry moves with it).
        var subject = WithEventPrefix(
            await EventDisplayNameAsync(eventId, ct), "A LinkedIn company-page post was published");
        var body =
            "<p>A scheduled LinkedIn company-page post has just been published.</p>"
            + $"<p><strong>Type:</strong> {post.Type}</p>"
            + $"<p><strong>Text:</strong><br/>{System.Net.WebUtility.HtmlEncode(post.EffectiveText)}</p>";

        foreach (var to in recipients)
        {
            try
            {
                // §326ca — RING-EXEMPT, stated explicitly. Goes to the configured SoMe
                // notification list (organizers), never a participant: it reports that a
                // company-page post already went live. Same reasoning as the pre-alert above.
                using (_emailContext?.Set(new EmailContext(
                    NotifyCategory, eventId, RingExempt: true)))
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
