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
    string? Blocker = null)
{
    /// <summary>
    /// §916 — the post's STATE in his words: planned · scheduled · published.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-06: <i>"also include the state of the post (planned/scheduled)"</i>.
    /// 🔒 The same three words the organizer list uses (§889), so the two views cannot describe the
    /// same post differently.
    /// </remarks>
    public string State =>
        IsPublished ? "published"
        : IsApproved ? "scheduled"
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

        var sessionKeys = sessionIds.Select(id => SessionPrefix + id.ToString()).ToHashSet();
        // The planner writes $"tier:{SponsorPackage}", so the key must be the enum's name.
        var tierKey = tier is null ? null : TierPrefix + tier.Value;
        var sponsorKey = SponsorPrefix + sponsorCompanyId;

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
                        && (p.SubjectKey == sponsorKey
                            || (tierKey != null && p.SubjectKey == tierKey)
                            || sessionKeys.Contains(p.SubjectKey)))
            .ToListAsync(ct);

        var all = await ProjectWithBlockersAsync(posts, ct);

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
        var sessions = await _db.SessionSpeakers
            .Where(ss => ss.ParticipantId == participantId && ss.Session.EventId == eventId)
            .Select(ss => new { ss.SessionId, ss.Session.Track })
            .ToListAsync(ct);

        var sessionKeys = sessions.Select(s => SessionPrefix + s.SessionId.ToString()).ToHashSet();
        var trackKeys = sessions
            .Where(s => !string.IsNullOrWhiteSpace(s.Track))
            .Select(s => TrackPrefix + s.Track!)
            .ToHashSet();

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
        var result = new List<SoMeAnnouncement>(posts.Count);

        foreach (var p in posts)
        {
            // A post already approved or published has nothing left to wait for — asking the gate
            // would spend a query to be told what its state already says.
            var blocker = p.IsActive || p.Status == SoMePostStatus.Published
                ? null
                : await gate.BlockedReasonAsync(p, ct);

            result.Add(Project([p])[0] with { Blocker = blocker });
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
            MentionLineFor(p))).ToList();

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
