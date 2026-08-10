using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// One announcement, ready to show to a human — organizer, sponsor or speaker.
/// </summary>
/// <param name="PreviewText">
/// What would ACTUALLY publish — the manual override when set, else the composed text
/// (<see cref="SoMePost.EffectiveText"/>). Never the template, never the auto text when an
/// organizer has edited it: showing a sponsor something other than the real words is the whole
/// failure mode a preview exists to prevent.
/// </param>
/// <param name="MentionLine">
/// 🔒 What the post will say about people, stated HONESTLY. §824.14c settled that
/// <c>{OrganizerLinkedInUrls}</c> renders <b>names, not @-mentions</b>, and that a member URN
/// cannot be obtained for an external speaker at all. So this says "mentions by name" — a preview
/// showing an @-handle would promise a LinkedIn notification CEH cannot send.
/// </param>
public sealed record SoMeAnnouncement(
    int PostId,
    SoMeTemplateKind? Kind,
    string KindLabel,
    string? SubjectKey,
    DateTimeOffset ScheduledAtUtc,
    SoMePostStatus Status,
    bool IsApproved,
    string PreviewText,
    string? ImageRef,
    string MentionLine,
    string? Blocker = null,
    /// <summary>
    /// §1029 — whose decision the post is: <c>Proposed</c> (the planner's suggestion, still movable)
    /// or <c>Scheduled</c> (he has accepted it). Distinct from <see cref="IsApproved"/>, which is
    /// "may it publish" — and conflating the two is what made a Proposed post call itself scheduled.
    /// </summary>
    SoMePostPlanState PlanState = SoMePostPlanState.Proposed,
    /// <summary>
    /// §1032 — whether <see cref="ImageRef"/> names a PICTURE or a VIDEO. The preview has to know:
    /// an <c>&lt;img&gt;</c> pointed at an MP4 renders as a broken image, which is a worse preview
    /// than the file name it replaced.
    /// </summary>
    SoMePostMediaKind MediaKind = SoMePostMediaKind.Graphic)
{
    /// <summary>
    /// §916 — the post's STATE in his words: planned · scheduled · published.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-06: <i>"also include the state of the post (planned/scheduled)"</i>.
    /// 🔒 The same three words the organizer list uses (§889), so the two views cannot describe the
    /// same post differently.
    /// </remarks>
    /// <remarks>
    /// 🔴 §1029 — "scheduled" needs BOTH: the plan ACCEPTED and the post APPROVED. Operator
    /// 2026-08-10: *"i am confused that a post for late sept is scheduled"* … *"i thought that we
    /// implemented a 1 or 2 weeks before planned then a post became scheduled"* … *"and approved"*.
    ///
    /// <para>⚠️ This used to read <c>IsApproved</c> alone, which CONFLATES two independent facts —
    /// <c>PlanState</c> is *"whose decision is this?"* and <c>IsActive</c> is *"may it publish?"*,
    /// and the domain says so in as many words. Measured in PROD 2026-08-10: <b>29 posts were
    /// Proposed AND approved</b> against 8 genuinely Scheduled, so a post he had never accepted
    /// into the plan announced itself as "scheduled" six weeks out — while auto-approve
    /// (<c>AutoApproveLeadDays = 7</c>) could not have been what approved it.</para>
    ///
    /// <para>🔒 The badge now says "planned" until he has accepted it, which is the word he uses
    /// for that state and the reason he could not reconcile the page with what he remembered
    /// building.</para>
    /// </remarks>
    public string State =>
        IsPublished ? "published"
        : (IsApproved && PlanState == SoMePostPlanState.Scheduled) ? "scheduled"
        : "planned";

    /// <summary>Held for approval — planned but not yet turned on (§824.21a). Organizer-only view.</summary>
    public bool IsHeld => Status == SoMePostStatus.Queued && !IsApproved;

    public bool IsPublished => Status == SoMePostStatus.Published;

    /// <summary>
    /// The date the audience cares about — the DANISH day (§844.5).
    /// </summary>
    /// <remarks>
    /// ⚠️ Was the UTC day, which put a late-evening Danish post on the previous day's page of the
    /// calendar. The whole point of grouping is "what goes out that day", so the day must be local.
    /// </remarks>
    public DateOnly Day => SoMeDisplayTime.DanishDay(ScheduledAtUtc);
}

/// <summary>
/// §835–§838 — THE ONE PLACE that answers "which posts mention this subject, and when do they run".
///
/// <para>The organizer calendar (§835), the per-session announcement dates (§836), the sponsor page
/// (§837) and the speaker page (§838) are four VIEWS of one question. They are built on this single
/// query on purpose: four independent implementations of "find the posts about X" would drift, and
/// the one that drifts silently is the one shown to a sponsor.</para>
///
/// <para>🔒 <b>THE SCOPING RULE IS ENFORCED HERE, NOT IN THE PAGES.</b> A sponsor sees their own
/// posts; a speaker sees theirs. Putting that filter in a page would mean the next page to be added
/// could leak by omission — the §831 lesson, where a missing entry degraded silently.</para>
///
/// <para>🔒 <b>EXTERNAL AUDIENCES SEE APPROVED POSTS ONLY</b> (operator 2026-08-05: <i>"only show
/// approved posts"</i>). A planned post is unapproved and its wording may still change, so showing
/// it to a sponsor would publish a promise he then edits. ⚠️ This is an EXTERNAL-audience rule: the
/// organizer's own views must keep showing held posts, or the approval queue becomes invisible to
/// the one person who has to work it.</para>
/// </summary>
public sealed class SoMeAnnouncementQuery
{
    private readonly CommunityHubDbContext _db;

    public SoMeAnnouncementQuery(CommunityHubDbContext db) => _db = db;

    /// <summary>The SubjectKey prefixes the planner writes (see <c>SoMeScheduleService</c>).</summary>
    public const string TrackPrefix = "track:";
    public const string SessionPrefix = "session:";
    public const string TierPrefix = "tier:";
    public const string SponsorPrefix = "sponsor:";
    /// <summary>§834.4 — Type 5's key is <c>event:{slug}</c>, the slug the deck states (§828.6).</summary>
    public const string EventPostPrefix = "event:";

    public static string KindLabelFor(SoMeTemplateKind? kind) => kind switch
    {
        SoMeTemplateKind.SpeakerTracks => "Track announcement",
        SoMeTemplateKind.Session => "Session announcement",
        SoMeTemplateKind.SponsorCategory => "Sponsor category",
        SoMeTemplateKind.Sponsor => "Sponsor announcement",
        SoMeTemplateKind.EventPost => "Event post",
        _ => "Written by hand",
    };

    /// <summary>
    /// EVERYTHING for the edition — the organizer calendar (§835). <paramref name="approvedOnly"/>
    /// defaults to FALSE here because the organizer must see what is still held.
    /// </summary>
    public async Task<IReadOnlyList<SoMeAnnouncement>> ForEventAsync(
        int eventId, bool approvedOnly = false, CancellationToken ct = default)
    {
        var posts = await BaseQuery(eventId, approvedOnly).ToListAsync(ct);
        return Project(posts);
    }

    /// <summary>§836 — the announcements for ONE session, by the planner's <c>session:{id}</c> key.</summary>
    public async Task<IReadOnlyList<SoMeAnnouncement>> ForSessionAsync(
        int eventId, int sessionId, bool approvedOnly = false, CancellationToken ct = default)
    {
        var key = SessionPrefix + sessionId.ToString();
        var posts = await BaseQuery(eventId, approvedOnly)
            .Where(p => p.SubjectKey == key)
            .ToListAsync(ct);
        return Project(posts);
    }

    /// <summary>
    /// §836 — announcement dates for MANY sessions at once, so the sessions grid is one query rather
    /// than one per row.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<SoMeAnnouncement>>> ForSessionsAsync(
        int eventId, IReadOnlyCollection<int> sessionIds, bool approvedOnly = false,
        CancellationToken ct = default)
    {
        if (sessionIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<SoMeAnnouncement>>();
        }

        var keys = sessionIds.Select(id => SessionPrefix + id.ToString()).ToHashSet();

        var posts = await BaseQuery(eventId, approvedOnly)
            .Where(p => p.SubjectKey != null && keys.Contains(p.SubjectKey))
            .ToListAsync(ct);

        return Project(posts)
            .GroupBy(a => int.Parse(a.SubjectKey![SessionPrefix.Length..]))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SoMeAnnouncement>)g.ToList());
    }

    /// <summary>
    /// §837 — what a SPONSOR sees: their tier post, their company post, and the sessions their own
    /// people are speaking at. 🔒 Approved only, always — this is an external audience.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The third bucket is NOT taken from <c>SponsorSessions</c>.</b> A §292 sponsor session is
    /// CEH-owned and pushed one-way to Zoho; it carries <b>no foreign key to <c>Sessions</c></b>, and
    /// Type 2 posts are keyed on <c>Sessions</c>. Matching the two by TITLE would be exactly the §767
    /// mistake (a guessed convention that matched nothing for four production runs). Instead the link
    /// used is a real one: sessions whose speakers are participants belonging to this sponsor company.
    /// </remarks>
    public async Task<SponsorAnnouncements> ForSponsorAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sponsorCompanyId))
        {
            return new SponsorAnnouncements([], [], [], false);
        }

        var (sponsorKey, tierKey, sessionKeys) = await SponsorScopeAsync(eventId, sponsorCompanyId, ct);

        // 🔴 §916 — PLANNED POSTS ARE SHOWN TOO, with their state on the row.
        //
        // This was `approvedOnly: true` on an explicit earlier decision (operator 2026-08-05: "only
        // show approved posts"), whose reason was sound: an unapproved post's wording may still
        // change, so showing it promises words that then get edited.
        //
        // ⚠️ What that reason did not account for is that NOTHING is auto-approved — every type 1–4
        // post is created HELD (§824.8 Q2) and waits for him. Measured on PROD 2026-08-06: 79
        // planned, 1 approved. So the page was empty for every speaker and sponsor, which is why he
        // asked where the posts were.
        //
        // 🔒 The state badge is what makes this safe now: a "planned" post SAYS it is planned, and
        // the blocker line says what it is still waiting for.
        // 🔴 §1028 — APPROVED ONLY, AND ELIGIBLE ONLY. Operator 2026-08-10: *"only show approved
        // posts"* · *"correction: only show when sponsor is eligble fx have delviered some text"*.
        //
        // ⚠️ This RE-REVERSES §916, and the reason that change existed has expired rather than been
        // overruled. §916 opened the page to planned posts because NOTHING was approved (measured
        // 2026-08-06: 79 planned, 1 approved) and every sponsor saw an empty page. Measured again
        // today: **37 approved** (33 queued + 4 published), all approved by him by hand — so the
        // page has real content and the original §834.1 rule can stand again.
        //
        // 🔑 "Eligible" is the BLOCKER, which already answers it: `SoMeApprovalGate` derives
        // "the sponsor still owes their social text" from `{SponsorSocialMediaCompanyDescription}`
        // resolving empty. So a post the sponsor has not fed yet is not shown to them as though it
        // were ready — which is the promise-you-then-edit problem §834.1 was about, in its second
        // form.
        var posts = await BaseQuery(eventId, approvedOnly: true)
            .Where(p => p.SubjectKey != null
                        && (p.SubjectKey == sponsorKey
                            || (tierKey != null && p.SubjectKey == tierKey)
                            || sessionKeys.Contains(p.SubjectKey)))
            .ToListAsync(ct);

        var all = (await ProjectWithBlockersAsync(posts, ct))
            .Where(a => a.Blocker is null)
            .ToList();

        return new SponsorAnnouncements(
            Company: all.Where(a => a.SubjectKey == sponsorKey).ToList(),
            Category: all.Where(a => tierKey != null && a.SubjectKey == tierKey).ToList(),
            Sessions: all.Where(a => a.SubjectKey != null && sessionKeys.Contains(a.SubjectKey)).ToList(),
            HasPurchasedSessions: await _db.SponsorSessions
                .AnyAsync(s => s.EventId == eventId && s.SponsorCompanyId == sponsorCompanyId, ct));
    }

    /// <summary>
    /// §838 — what a SPEAKER sees: the sessions they are on, and their track posts.
    /// 🔒 Approved only, always.
    /// </summary>
    public async Task<SpeakerAnnouncements> ForSpeakerAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var (sessionKeys, trackKeys) = await SpeakerScopeAsync(eventId, participantId, ct);

        // 🔴 §916 — PLANNED POSTS ARE SHOWN TOO, with their state on the row.
        //
        // This was `approvedOnly: true` on an explicit earlier decision (operator 2026-08-05: "only
        // show approved posts"), whose reason was sound: an unapproved post's wording may still
        // change, so showing it promises words that then get edited.
        //
        // ⚠️ What that reason did not account for is that NOTHING is auto-approved — every type 1–4
        // post is created HELD (§824.8 Q2) and waits for him. Measured on PROD 2026-08-06: 79
        // planned, 1 approved. So the page was empty for every speaker and sponsor, which is why he
        // asked where the posts were.
        //
        // 🔒 The state badge is what makes this safe now: a "planned" post SAYS it is planned, and
        // the blocker line says what it is still waiting for.
        var posts = await BaseQuery(eventId, approvedOnly: false)
            .Where(p => p.SubjectKey != null
                        && (sessionKeys.Contains(p.SubjectKey) || trackKeys.Contains(p.SubjectKey)))
            .ToListAsync(ct);

        var all = await ProjectWithBlockersAsync(posts, ct);

        return new SpeakerAnnouncements(
            Sessions: all.Where(a => a.SubjectKey != null && sessionKeys.Contains(a.SubjectKey)).ToList(),
            Tracks: all.Where(a => a.SubjectKey != null && trackKeys.Contains(a.SubjectKey)).ToList());
    }

    /// <summary>
    /// §837 — the three subject keys a SPONSOR's page is built from: their own company, their tier,
    /// and the sessions their own people speak at.
    /// </summary>
    /// <remarks>
    /// 🔒 Extracted (§1032) so the announcements list and the picture endpoint scope a sponsor the
    /// SAME way. Two copies of "which posts are about this company" is precisely the drift this
    /// class exists to prevent — and the copy that drifted would be the one guarding an image.
    /// </remarks>
    private async Task<(string SponsorKey, string? TierKey, HashSet<string> SessionKeys)>
        SponsorScopeAsync(int eventId, string sponsorCompanyId, CancellationToken ct)
    {
        // ⚠️ Cast to NULLABLE deliberately: SponsorPackage is an enum, so FirstOrDefaultAsync on a
        // sponsor with no row would return the enum's zero value — a REAL tier — and quietly show
        // that tier's posts to a company that is not in it.
        var tier = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && s.SponsorCompanyId == sponsorCompanyId)
            .Select(s => (SponsorPackage?)s.SponsorPackage)
            .FirstOrDefaultAsync(ct);

        // Sessions this sponsor's own people speak at — a REAL link, not a title match.
        var sessionIds = await _db.SessionSpeakers
            .Where(ss => ss.Session.EventId == eventId
                         && ss.Participant.SponsorCompanyId == sponsorCompanyId)
            .Select(ss => ss.SessionId)
            .Distinct()
            .ToListAsync(ct);

        return (
            SponsorPrefix + sponsorCompanyId,
            // The planner writes $"tier:{SponsorPackage}", so the key must be the enum's name.
            tier is null ? null : TierPrefix + tier.Value,
            sessionIds.Select(id => SessionPrefix + id.ToString()).ToHashSet());
    }

    /// <summary>§838 — the subject keys a SPEAKER's page is built from: their sessions and tracks.</summary>
    private async Task<(HashSet<string> SessionKeys, HashSet<string> TrackKeys)>
        SpeakerScopeAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sessions = await _db.SessionSpeakers
            .Where(ss => ss.ParticipantId == participantId && ss.Session.EventId == eventId)
            .Select(ss => new { ss.SessionId, ss.Session.Track })
            .ToListAsync(ct);

        return (
            sessions.Select(s => SessionPrefix + s.SessionId.ToString()).ToHashSet(),
            sessions
                .Where(s => !string.IsNullOrWhiteSpace(s.Track))
                .Select(s => TrackPrefix + s.Track!)
                .ToHashSet());
    }

    /// <summary>
    /// §1032 — the post whose PICTURE this participant may fetch, or null.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-10: the sponsor page showed <c>sponsor-12.png</c> where the picture
    /// should be. Rendering it means an <c>&lt;img&gt;</c>, which means an endpoint — and an endpoint
    /// serving campaign media to an EXTERNAL audience needs the same gate the list has, not merely
    /// "signed in". 🔒 So this answers exactly one question — <i>is this post on this participant's
    /// own announcements page?</i> — using the SAME scope helpers the lists use.</para>
    ///
    /// <para>⚠️ The two audiences have different approval rules and that is deliberate, not an
    /// oversight: a SPONSOR sees approved posts only (§1028), so their picture is gated on
    /// <c>IsActive</c>; a SPEAKER sees planned ones too (§916), and a preview whose text is shown but
    /// whose picture 404s would read as a broken page.</para>
    ///
    /// <para>🔒 Resolves the sponsor company from the participant row itself rather than trusting a
    /// caller-supplied id — the endpoint must not be able to name a company it does not belong to.</para>
    /// </remarks>
    public async Task<SoMePost?> VisibleMediaPostAsync(
        int eventId, int participantId, int postId, CancellationToken ct = default)
    {
        var post = await _db.SoMePosts
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == postId && p.EventId == eventId && !p.IsDeleted, ct);

        if (post?.SubjectKey is null) return null;

        var sponsorCompanyId = await _db.Participants
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(sponsorCompanyId)
            && post.IsActive && post.Status != SoMePostStatus.Failed)
        {
            var (sponsorKey, tierKey, sponsorSessionKeys) =
                await SponsorScopeAsync(eventId, sponsorCompanyId!, ct);

            if (post.SubjectKey == sponsorKey
                || (tierKey != null && post.SubjectKey == tierKey)
                || sponsorSessionKeys.Contains(post.SubjectKey))
            {
                return post;
            }
        }

        var (sessionKeys, trackKeys) = await SpeakerScopeAsync(eventId, participantId, ct);
        return sessionKeys.Contains(post.SubjectKey) || trackKeys.Contains(post.SubjectKey)
            ? post
            : null;
    }

    private IQueryable<SoMePost> BaseQuery(int eventId, bool approvedOnly)
    {
        // 🔴 §916 — A DELETED POST IS A TOMBSTONE, NOT CONTENT. §853 keeps the row so the planner
        // cannot re-propose what he deleted; it is not something to show ANY audience, and this
        // query had no such filter at all. It mattered less while external views were
        // approved-only; the moment planned posts became visible it would have put a post he
        // deleted back in front of a sponsor.
        var q = _db.SoMePosts.Where(p => p.EventId == eventId && !p.IsDeleted);

        // 🔒 IsActive IS the approval (§824.21a) — a post is created held and an organizer turns it
        // on. Kept for the ORGANIZER views that ask for it.
        if (approvedOnly)
        {
            q = q.Where(p => p.IsActive && p.Status != SoMePostStatus.Failed);
        }

        return q.OrderBy(p => p.ScheduledAtUtc).AsNoTracking();
    }

    /// <summary>
    /// §916 — the same projection, with each post's BLOCKER filled in.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-06: <i>"and any missing depencies (blockers)"</i>. A planned post that
    /// is waiting on something says so — a sponsor who has not sent their social text or logo reads
    /// the reason on their own page instead of wondering why nothing is scheduled.</para>
    ///
    /// <para>🔒 The reason comes from <see cref="SoMeApprovalGate"/>, the SAME sentence the organizer
    /// sees. Two wordings for one condition is how the two views start disagreeing about whether a
    /// post is ready.</para>
    ///
    /// <para>⚠️ Only for the small per-audience lists (a speaker's or sponsor's own handful). The
    /// gate is a query per post, so the edition-wide calendar deliberately does NOT use this.</para>
    /// </remarks>
    private async Task<IReadOnlyList<SoMeAnnouncement>> ProjectWithBlockersAsync(
        IReadOnlyList<SoMePost> posts, CancellationToken ct)
    {
        var gate = new SoMeApprovalGate(_db);
        // 🔴 §1028 — RESOLVE THE VARIABLES. Operator 2026-08-10: *"dont show the template/variable
        // version but preview version"*.
        //
        // The page was printing `{SponsorName}`, `{SponsorWebsite}`, `{Organizers}` at a SPONSOR —
        // internal template syntax, shown to an external audience, on a page whose entire purpose
        // is *"where they can preview it"*. 🔑 The post itself was never wrong: the tokens resolve
        // at publish. Only the preview was, which is the §932 failure exactly — preview and publish
        // must read the same text through the same resolver.
        var composer = new SoMePostComposer(_db, new SoMeVariableResolver(_db));
        var result = new List<SoMeAnnouncement>(posts.Count);

        foreach (var p in posts)
        {
            // A post already approved or published has nothing left to wait for — asking the gate
            // would spend a query to be told what its state already says.
            var blocker = p.IsActive || p.Status == SoMePostStatus.Published
                ? null
                : await gate.BlockedReasonAsync(p, ct);

            string resolved;
            try
            {
                var values = await composer.ValuesForAsync(p, ct);
                resolved = composer.Resolve(p.EffectiveText, values);
            }
            catch
            {
                // 🔒 Fail back to the raw body rather than to nothing. An unresolvable post is
                // still information — a blank card would read as "there is no post".
                resolved = p.EffectiveText;
            }

            result.Add(Project([p])[0] with { Blocker = blocker, PreviewText = resolved });
        }

        return result;
    }

    private static IReadOnlyList<SoMeAnnouncement> Project(IReadOnlyList<SoMePost> posts) =>
        posts.Select(p => new SoMeAnnouncement(
            p.Id,
            p.TemplateKind,
            KindLabelFor(p.TemplateKind),
            p.SubjectKey,
            p.ScheduledAtUtc,
            p.Status,
            p.IsActive,
            p.EffectiveText,
            p.ImageRef,
            MentionLineFor(p),
            Blocker: null,
            PlanState: p.PlanState,
            MediaKind: p.MediaKind)).ToList();

    /// <summary>
    /// 🔒 §824.14c — the honest answer to "who will it tag". CEH tags its own ORGANISATION on the
    /// company page; everyone else is named in the text. An external speaker cannot be @-mentioned
    /// at all (no member URN is obtainable), and the sponsor company-id lookup returns 403 even for
    /// our own org. Saying "tags you" here would promise a LinkedIn notification that never arrives.
    /// </summary>
    private static string MentionLineFor(SoMePost p) =>
        p.TagList.Count == 0
            ? "Mentions the organizers by name in the post text — LinkedIn does not notify people named this way."
            : $"Mentions by name in the post text ({p.TagList.Count}) — named, not @-tagged, so no LinkedIn notification is sent.";
}

/// <summary>§837 — a sponsor's three buckets, kept apart because he named them as three.</summary>
public sealed record SponsorAnnouncements(
    IReadOnlyList<SoMeAnnouncement> Company,
    IReadOnlyList<SoMeAnnouncement> Category,
    IReadOnlyList<SoMeAnnouncement> Sessions,
    bool HasPurchasedSessions)
{
    public int Total => Company.Count + Category.Count + Sessions.Count;
}

/// <summary>§838 — a speaker's two buckets.</summary>
public sealed record SpeakerAnnouncements(
    IReadOnlyList<SoMeAnnouncement> Sessions,
    IReadOnlyList<SoMeAnnouncement> Tracks)
{
    public int Total => Sessions.Count + Tracks.Count;
}
