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
        Microsoft.Extensions.Options.IOptions<EmailOptions>? emailOptions = null,
        // §844: optional media library — resolves a post's ImageRef BY FILE NAME from the graphics
        // or videos folder for its type. §828/§844 posts store a bare NAME, not a GraphicAssets row,
        // so without this a Type 5 or video post would publish text-only. Absent (tests) ⇒ only the
        // legacy GraphicAssets path resolves, exactly as before.
        SoMeGraphicLibrary? mediaLibrary = null,
        // §850 — eligibility re-checked at SEND time. Optional so existing test constructions keep
        // working; absent ⇒ no gate, exactly as before.
        SoMeApprovalGate? approvalGate = null,
        // §864 — resolves {tokens} in the body AT PUBLISH TIME, so a sponsor's late text edit or a
        // newly-linked speaker reaches a post planned months ago. Optional so existing test
        // constructions keep working; absent ⇒ the body publishes as stored, exactly as before.
        SoMePostComposer? composer = null)
    {
        _composer = composer;
        _approvalGate = approvalGate;
        _mediaLibrary = mediaLibrary;
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
    private readonly SoMeGraphicLibrary? _mediaLibrary;
    private readonly SoMeApprovalGate? _approvalGate;

    /// <summary>§864 — token resolution at publish time. Null in tests ⇒ body publishes as stored.</summary>
    private readonly SoMePostComposer? _composer;

    /// <summary>
    /// §324: resolve a post's ImageRef (the graphic's SharePoint URL/path) to raw
    /// bytes via the GraphicAsset's stored item id. Fail-soft null (text-only post)
    /// when unresolvable — never blocks the publish on a missing image source.
    /// </summary>
    private async Task<(byte[]? Bytes, string? Alt)> ResolveImageAsync(
        SoMePost post, CancellationToken ct)
    {
        // 🔴 §917 — THE SUBJECT'S CURRENT GRAPHIC WINS OVER THE ONE STAMPED AT PLAN TIME.
        //
        // Operator 2026-08-06: *"the picture … takes the current list of speaker - and not stamp
        // them at planning time. so i dont have to worry about wrong speaker assigned"*. §901
        // already gave the WORDS that property; §915 accidentally took it away from the PICTURE by
        // copying the file name onto the post when it was planned.
        //
        // ⚠️ The session graphic is the case that breaks: one speaker renders `session-12.png`,
        // two render `session-12.gif` (§767 — the extension carries single-vs-multi). A second
        // speaker joining a session leaves the stamped `.png` pointing at artwork that no longer
        // shows the line-up. §767 Round 8 named this exactly: "a queued SoMe post must point at the
        // asset ROW and not a copied path".
        //
        // 🔒 Types 1–4 ONLY (SoMeSubjectGraphic.IsSubjectOwned). Type 5 names its own file in the
        // deck and an ad-hoc post has no subject — for those the stored ref IS the answer, and
        // overriding it would discard a choice somebody made deliberately.
        var imageRef = post.ImageRef;
        var current = await new SoMeSubjectGraphic(_db).CurrentFileNameAsync(post, ct);
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, imageRef, StringComparison.OrdinalIgnoreCase))
        {
            _log?.LogInformation(
                "§917: post {PostId} publishes with its subject's CURRENT graphic {Current} "
                + "(the post was planned with {Stamped}).", post.Id, current, imageRef ?? "none");
            imageRef = current;
        }

        if (string.IsNullOrWhiteSpace(imageRef)) return (null, null);

        try
        {
            // The legacy path: a generated graphic tracked as a GraphicAssets row, referenced by
            // its SharePoint URL or path.
            if (_fileStore is { CanRead: true })
            {
                var graphic = await _db.GraphicAssets.AsNoTracking().FirstOrDefaultAsync(
                    g => g.EventId == post.EventId
                         && (g.SharePointUrl == imageRef || g.SharePointPath == imageRef),
                    ct);

                if (graphic?.StorageItemId is { Length: > 0 } itemId)
                {
                    var bytes = await _fileStore.DownloadAsync(itemId, ct);
                    if (bytes is { Length: > 0 }) return (bytes, "Session graphic");
                }
            }

            // §828/§844: a bare FILE NAME in the library folder for this post's type. This is how
            // every imported event post and every hand-picked graphic is stored.
            if (_mediaLibrary is not null)
            {
                var file = await _mediaLibrary.GetAsync(
                    imageRef, post.TemplateKind, Domain.SoMePostMediaKind.Graphic, ct);
                if (file is not null) return (file.Content, "Experts Live Denmark");
            }

            return (null, null);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SoMeDispatch: image resolve failed for post {PostId}.", post.Id);
            return (null, null);
        }
    }

    /// <summary>
    /// §844 — the VIDEO bytes for a post whose medium is Video, or null.
    /// </summary>
    /// <remarks>
    /// 🔒 §844.2 — when a post is marked Video and the file cannot be fetched, this returns null and
    /// <see cref="ResolveImageAsync"/> is NOT consulted as a substitute: the caller refuses to
    /// publish instead. Falling back to the graphic would quietly ship the asset he chose against.
    /// </remarks>
    private async Task<byte[]?> ResolveVideoAsync(SoMePost post, CancellationToken ct)
    {
        if (post.MediaKind != Domain.SoMePostMediaKind.Video
            || string.IsNullOrWhiteSpace(post.ImageRef)
            || _mediaLibrary is null)
        {
            return null;
        }

        try
        {
            var file = await _mediaLibrary.GetAsync(
                post.ImageRef, post.TemplateKind, Domain.SoMePostMediaKind.Video, ct);
            return file?.Content;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SoMeDispatch: video resolve failed for post {PostId}.", post.Id);
            return null;
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

        // 🔒 §861 — THE ORGANIZER CREDIT IS COMPOSED HERE, AT PUBLISH TIME, AND NEVER STORED.
        // Read once per pass, not per post. This is what lets a credit that becomes an @-mention
        // (§858) reach posts he has ALREADY EDITED — a stored credit could not, which is exactly
        // the state posts 383 and 495 were in (§861.3).
        var editionCode = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => e.Code)
            .FirstOrDefaultAsync(ct);

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

        // §922 — "waiting" is its own outcome: not published, not failed, nothing wrong with it.
        int published = 0, failed = 0, withdrawn = 0, waiting = 0;
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

                // 🔒 §850 — RE-CHECK ELIGIBILITY AT SEND TIME, not only at approval.
                //
                // Approval is a MOMENT; eligibility is a STATE. A post approved while the sponsor's
                // social-media text existed, and then the text cleared, must not publish — so the
                // gate is consulted here as well as at the point of approval.
                if (_approvalGate is not null
                    && await _approvalGate.BlockedReasonAsync(post, ct) is { } notEligible)
                {
                    post.LastError = notEligible;
                    _log?.LogWarning(
                        "SoMeDispatch: post {PostId} is approved but no longer eligible — {Reason}",
                        post.Id, notEligible);
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                // §844 — VIDEO FIRST when the post is a video post (§844.2: "we prefer videos more
                // than graphics"), else §324's native image upload.
                var videoBytes = await ResolveVideoAsync(post, ct);

                byte[]? imageBytes = null;
                string? imageAlt = null;

                if (videoBytes is null && post.MediaKind == Domain.SoMePostMediaKind.Video)
                {
                    // 🔒 REFUSE rather than fall back. He marked this post as a video; publishing
                    // its graphic instead would ship the asset he chose against, and nothing would
                    // tell him it happened. Left queued, with the reason recorded.
                    post.LastError = "This post is set to publish a video, but the video file could "
                                   + "not be read from the library. It was NOT published as a "
                                   + "graphic — fix the linked file and it will go out on a later run.";
                    _log?.LogWarning(
                        "SoMeDispatch: post {PostId} is a VIDEO post but its file '{File}' could not "
                        + "be read; refusing to downgrade to the graphic.", post.Id, post.ImageRef);
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                if (videoBytes is null)
                {
                    (imageBytes, imageAlt) = await ResolveImageAsync(post, ct);
                }

                // 🔒 §864 — THE TEXT IS BUILT HERE, AT PUBLISH TIME, NOT AT PLAN TIME.
                // This is what makes a sponsor's late edit to their social text, or a second speaker
                // linked to a session, reach a post that was planned months earlier (§848 plans to
                // 2027-02). Composing at plan time froze all of it.
                var body = post.EffectiveText;
                if (_composer is not null)
                {
                    var values = await _composer.ValuesForAsync(post, ct);

                    // ⚠️ REPORTED, NEVER FATAL (§864.3). An UNKNOWN token — a typo nothing can
                    // resolve — survives verbatim and is logged: refusing would withhold the post
                    // for ever and tell nobody, which is worse than a visible oddity.
                    var unresolved = SoMePostComposer.UnresolvedTokens(body, values);
                    if (unresolved.Count > 0)
                    {
                        _log?.LogWarning(
                            "SoMeDispatch: post {PostId} publishes with {Count} unresolved token(s): "
                            + "{Tokens}. The post still goes out — the token text is left as written.",
                            post.Id, unresolved.Count, string.Join(", ", unresolved));
                    }

                    // 🔴 §922 — BUT A KNOWN VARIABLE WITH NO VALUE STOPS THE POST.
                    //
                    // Operator 2026-08-06: *"a post can NOT go out if a variable is empty in the
                    // post. that is the blocker."* The check runs HERE as well as at approval
                    // because everything resolves late (§901/§917): a post that was complete when
                    // he approved it can become incomplete afterwards — a speaker removed, a
                    // sponsor's text cleared — and approval would otherwise be a stale snapshot of
                    // readiness.
                    //
                    // 🔒 Left QUEUED, not failed: nothing is wrong with the post, something it
                    // depends on is missing. It publishes by itself once the value arrives, which
                    // is the whole point of resolving late.
                    if (SoMeEmptyVariableGate.ReasonFor(body, values) is { Length: > 0 } incomplete)
                    {
                        _log?.LogWarning(
                            "§922: post {PostId} NOT published — {Reason}. It stays queued and goes "
                            + "out on a later tick once the value exists.", post.Id, incomplete);
                        post.LastError = Truncate($"Waiting: {incomplete}", 2000);
                        await _db.SaveChangesAsync(ct);
                        waiting++;
                        continue;
                    }

                    body = _composer.Resolve(body, values);
                }

                // 🔑 §888.3 — THE ORGANIZER CREDIT IS AN ORDINARY VARIABLE. Nothing is appended here.
                // It used to be stapled on at publish time, which made it the one value that was not
                // in the body, not editable, not movable — and, because it bypassed the variable
                // pipeline, not eligible for the §858 mention resolution either. It published as
                // plain text while {Speakers} tagged people properly.
                // Operator 2026-08-06: *"remove the crap you build for organizer and make it as a
                // variable like others"*. The token lives in the template and in every body; it
                // resolves through the same path as every other variable.
                var textToPublish = body;

                var result = await _publisher.PublishAsync(
                    new LinkedInPost(page!, textToPublish,
                        post.ImageRef, post.TagList,
                        imageBytes, imageAlt, AccessTokenOverride: null, VideoBytes: videoBytes), ct);

                if (result.Published)
                {
                    post.Status = SoMePostStatus.Published;
                    post.PublishedAtUtc = _clock.GetUtcNow();
                    post.ExternalPostId = result.ExternalPostId;
                    post.LastError = null;
                    published++;
                    await _db.SaveChangesAsync(ct);
                    // §865.3 — the COMPOSED text is passed in, not re-read from the post. The mail
                    // must show what actually went to LinkedIn (body + resolved tokens + credit),
                    // and post.EffectiveText is only the stored body (§861).
                    await NotifyPublishedAsync(eventId, settings!, post, textToPublish, ct);
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
                  + (waiting > 0 ? $"{waiting} waiting on a missing value, " : string.Empty)
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
        int eventId, SoMeSettings settings, SoMePost post, string publishedText,
        CancellationToken ct)
    {
        if (!settings.NotifyOnPublish) return;
        var recipients = settings.NotificationEmailList;

        // ⚠️ §865.3 — NO RECIPIENTS MEANS NOBODY IS TOLD, and that state is invisible: NotifyOnPublish
        // was TRUE on PROD while the list was EMPTY, so the feature read as on and notified no one.
        // Say so in the log rather than returning in silence — the §854 shape.
        if (recipients.Count == 0)
        {
            _log?.LogWarning(
                "SoMeDispatch: post {PostId} published, but NO publish notification was sent — "
                + "NotifyOnPublish is on and the notification e-mail list is EMPTY. "
                + "Set it on /Organizer/SoMeSettings.", post.Id);
            return;
        }

        // §707.27 C3 — the EVENT NAME, not "[SoMe]" (see the pre-alert above; the catalog's
        // InlineSubjects entry moves with it).
        var subject = WithEventPrefix(
            await EventDisplayNameAsync(eventId, ct), "A LinkedIn company-page post was published");

        // 🔑 §865.3 — operator asked for "link to post + graphics + text".
        // The LINK is built from the urn LinkedIn returned (§859 proved it is stored in
        // ExternalPostId); the GRAPHIC is named and, where the library can read it, attached;
        // the TEXT is what actually published, credit and resolved tokens included.
        var postUrl = string.IsNullOrWhiteSpace(post.ExternalPostId)
            ? null
            : $"https://www.linkedin.com/feed/update/{post.ExternalPostId}";

        var body =
            "<p>A scheduled LinkedIn company-page post has just been published.</p>"
            + $"<p><strong>Type:</strong> {post.Type}</p>"
            + $"<p><strong>Scheduled:</strong> {SoMeDisplayTime.ToDanish(post.ScheduledAtUtc):ddd dd MMM yyyy HH:mm} (Danish time)</p>";

        if (postUrl is not null)
        {
            body += $"<p><strong>See it on LinkedIn:</strong> <a href=\"{postUrl}\">{postUrl}</a></p>";
        }
        else
        {
            // Published but no urn ⇒ say so rather than omitting the line, which would read as if
            // the post had no link rather than as if we failed to record one.
            body += "<p><strong>See it on LinkedIn:</strong> "
                  + "(no post id was returned, so no direct link is available)</p>";
        }

        body += string.IsNullOrWhiteSpace(post.ImageRef)
            ? "<p><strong>Media:</strong> none — this was a text-only post.</p>"
            : $"<p><strong>Media:</strong> {System.Net.WebUtility.HtmlEncode(post.ImageRef)} "
              + $"({(post.MediaKind == SoMePostMediaKind.Video ? "video" : "graphic")})</p>";

        body += "<p><strong>Text as published:</strong><br/>"
              + $"{System.Net.WebUtility.HtmlEncode(publishedText).Replace("\n", "<br/>")}</p>";

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
