using System.Globalization;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §824.16 — fills the <c>{Variable}</c> values a SoMe template needs, from CEH's own data.
/// </summary>
/// <remarks>
/// <para>Read-only and edition-scoped. It answers "what goes in the blanks for THIS post", and
/// nothing else: composing, scheduling and publishing all live elsewhere, so a wording change never
/// reaches into the data layer and a data fix never rewrites a template.</para>
///
/// <para>🔒 <b>Every value degrades to empty rather than to a guess.</b> A session with no track, a
/// sponsor with no description, an edition with no tag block — all ordinary states. The renderer
/// closes the gaps they leave (§824.15), so the post reads correctly with any of them missing. What
/// must never happen is a fabricated value: an invented tier or a wrong company name on a post to
/// 400+ followers is worse than a shorter post.</para>
/// </remarks>
public sealed class SoMeVariableResolver
{
    private readonly CommunityHubDbContext _db;

    public SoMeVariableResolver(CommunityHubDbContext db) => _db = db;

    /// <summary>One speaker as the mention list needs them: our name, plus their cached URN if any.</summary>
    internal sealed record SpeakerNameAndUrn(string FullName, string? PersonUrn);

    /// <summary>
    /// Attach each speaker's cached mention URN. Deliberately a SECOND query joined in memory
    /// rather than a correlated sub-select: the speaker list is tiny, and a per-row subquery is
    /// both poor SQL and unsupported over a <c>Distinct</c> projection.
    /// </summary>
    private async Task<List<SpeakerNameAndUrn>> WithMentionUrnsAsync(
        IReadOnlyList<(int ParticipantId, string FullName)> speakers, int? eventId, CancellationToken ct)
    {
        if (speakers.Count == 0) return new List<SpeakerNameAndUrn>();

        var ids = speakers.Select(s => s.ParticipantId).Distinct().ToList();

        // §884.1 — the URN lives on the PERSON now, so this is the same lookup for a speaker, a
        // sponsor signer or a coordinator.
        var urns = await _db.Participants
            .Where(p => ids.Contains(p.Id) && p.LinkedInPersonUrn != null)
            .Select(p => new { ParticipantId = p.Id, p.LinkedInPersonUrn })
            .ToListAsync(ct);

        var byParticipant = urns
            .GroupBy(u => u.ParticipantId)
            .ToDictionary(g => g.Key, g => g.First().LinkedInPersonUrn);

        return speakers
            .Select(s => new SpeakerNameAndUrn(
                s.FullName,
                byParticipant.TryGetValue(s.ParticipantId, out var urn) ? urn : null))
            .ToList();
    }

    /// <summary>
    /// §858.16 — <c>{Speakers}</c>: the same people as <c>{SpeakerNames}</c>, but each rendered as a
    /// real LinkedIn mention when we hold their person URN, and as their PLAIN FULL NAME otherwise.
    ///
    /// <para>🔒 <b>The fallback is not ours to remove.</b> LinkedIn only mentions members who FOLLOW the
    /// page, and it enforces that at post time — a valid URN for a non-follower publishes as a bare
    /// name (§858.16d). Measured on the real roster: 16 of 22 speakers are mentionable, so roughly one
    /// in four ALWAYS lands here.</para>
    ///
    /// <para>🔒 <b>No network call.</b> The URN is filled by the scheduled
    /// <c>SpeakerMentionResolutionService</c>; this only reads it. The endpoint carries a DAY
    /// throttle and a URN never changes, so resolving here would be both slow and wrong.</para>
    ///
    /// <para>⚠️ <c>{SpeakerNames}</c> is deliberately left alone — it is the untagged variant for copy
    /// where mentions are not wanted (§858.4). Two spellings, one rule.</para>
    /// </summary>
    private static string? MentionList(IReadOnlyList<SpeakerNameAndUrn> people)
    {
        if (people.Count == 0) return null;

        var rendered = people.Select(p =>
            string.IsNullOrWhiteSpace(p.PersonUrn)
                ? p.FullName
                : PersonMentionMatcher.RenderMention(p.FullName, p.PersonUrn!));

        return string.Join(" | ", rendered);
    }

    /// <summary>
    /// The values shared by every post of an edition — the footer, the dates, the venue.
    /// </summary>
    public async Task<Dictionary<string, string?>> EditionValuesAsync(
        int eventId, CancellationToken ct = default)
    {
        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Code, e.DisplayName, e.StartDate, e.EndDate, e.VenueName })
            .FirstOrDefaultAsync(ct);

        var settings = await _db.SoMeSettings
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.EventSystemUrl, s.EventTags, s.OrganizerCredits, s.EventVenueCityCountry })
            .FirstOrDefaultAsync(ct);

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["EditionCode"]            = ev?.Code,
            ["EventDisplayName"]       = ev?.DisplayName,
            // §888.2 — his names for the same two values. 🔒 The OLD spellings keep resolving on
            // purpose: {EditionCode} and {EventDisplayName} are in stored bodies and in the credit
            // label, so retiring either before those are migrated would publish a literal
            // "{EditionCode}" into a live post.
            ["EventNameShort"]         = ev?.Code,
            ["EventNameLong"]          = ev?.DisplayName,
            // §888.2 — typed on SoMe settings, NOT split out of VenueName: guessing a city from a
            // venue name would be a fabricated value on a live post (§824.16).
            ["EventVenueCityCountry"]  = settings?.EventVenueCityCountry,
            ["EventVenue"]             = ev?.VenueName,
            ["EventDates"]             = ev is null ? null : FormatDates(ev.StartDate, ev.EndDate),
            ["EventSystemUrl"]         = settings?.EventSystemUrl,
            ["EventTags"]              = settings?.EventTags,
            // §888.3 — the organizers, MENTIONED where we can (operator 2026-08-06: *"you have to
            // pass the variable for organizers to the mention solution we build"*). His credit
            // string stays the source of WHO and in WHAT ORDER; each name is only enriched with a
            // URN when we hold one, and otherwise reads exactly as he typed it.
            ["OrganizerLinkedInUrls"]  = await OrganizerMentionsAsync(settings?.OrganizerCredits, ct),
        };
    }

    /// <summary>
    /// §888.3 — turn the organizer credit line into real LinkedIn mentions.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>Driven by HIS list, not by a role query.</b> <c>OrganizerCredits</c> decides who appears
    /// and in which order — a role query would silently add or drop people from a line he curates.
    /// Each name is matched to a participant only to find a cached URN.
    /// <para>🔒 Same follower rule as everywhere else: LinkedIn mentions only people who follow the
    /// page, so anyone unmatched keeps their plain name and the line still reads correctly.</para>
    /// </remarks>
    private async Task<string?> OrganizerMentionsAsync(string? credits, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credits)) return credits;

        var names = credits.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .ToList();
        if (names.Count == 0) return credits;

        var known = await _db.Participants
            .Where(p => p.LinkedInPersonUrn != null)
            .Select(p => new { p.FullName, p.LinkedInPersonUrn })
            .ToListAsync(ct);

        // 🔒 §888.3a — MATCH ON THE FOLDED NAME, not the raw string. The credit line carries how he
        // WRITES a name ("Morten Waltorp Knudsen [MVP]"); the participant record carries the name
        // itself. An exact comparison silently failed on exactly one person — him — and he is the
        // one who would have noticed on the published post. Folding also handles the Danish letters
        // that differ between the two sources.
        var byName = known
            .GroupBy(k => PersonMentionMatcher.Fold(k.FullName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().LinkedInPersonUrn, StringComparer.OrdinalIgnoreCase);

        var rendered = names.Select(n =>
            byName.TryGetValue(PersonMentionMatcher.Fold(n), out var urn) && !string.IsNullOrWhiteSpace(urn)
                ? PersonMentionMatcher.RenderMention(n, urn!)
                : n);

        return string.Join(" | ", rendered);
    }

    /// <summary>
    /// Type 1 — one track's values: its name and the speakers on it.
    /// </summary>
    /// <remarks>
    /// Speakers are listed pipe-separated in the ELDK26 house style. ⚠️ The names are CEH's, not the
    /// speakers' LinkedIn profile names — the sample's `🛡️ Seyfallah Tagrerout☁ [MVP and RD]` is a
    /// person's own profile string, which CEH does not hold and must not invent (§824.8b).
    /// </remarks>
    public async Task<Dictionary<string, string?>> TrackValuesAsync(
        int eventId, string track, CancellationToken ct = default)
    {
        var speakers = await _db.SessionSpeakers
            .Where(ss => ss.Session!.EventId == eventId
                         && ss.Session.Track == track
                         && !ss.Session.IsServiceSession
                         && ss.Participant != null
                         && ss.Participant.IsActive
                         // 🔴 §905 — never a test account. IsActive was NOT enough: a test user is
                         // normally ACTIVE (that is the point of the fixture), so the two test
                         // speakers only stayed out of this list by the accident of being
                         // deactivated as well.
                         && !ss.Participant.IsTestUser)
            .Select(ss => new { ss.ParticipantId, ss.Participant!.FullName })
            .Distinct()
            .OrderBy(x => x.FullName)
            .ToListAsync(ct);

        var people = await WithMentionUrnsAsync(
            speakers.Select(s => (s.ParticipantId, s.FullName)).ToList(), eventId, ct);

        var names = people.Select(p => p.FullName).ToList();

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TrackName"]    = track,
            // §908 — HIS spelling, from the wordings he wrote: {SpeakerTrack}. Both resolve to the
            // same value rather than becoming two mechanisms that drift apart — the same call
            // §888.4 made for {Organizers} vs {OrganizerLinkedInUrls} (§858.4b: "no need to have 2
            // or 3 similar"). 🔒 The OLD name keeps working because it is in stored bodies already.
            ["SpeakerTrack"] = track,
            // Empty rather than "no speakers yet": the line simply does not print, and the post is
            // still a correct track announcement.
            ["SpeakerNames"] = names.Count == 0 ? null : string.Join(" | ", names),
            ["Speakers"]     = MentionList(people),
        };
    }

    /// <summary>Type 2 — one session's values.</summary>
    public async Task<Dictionary<string, string?>> SessionValuesAsync(
        int sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions
            .Where(s => s.Id == sessionId)
            // §824.2D — the Abstract comes along as the context the AI intro is written FROM
            // ("based on session title and session abstract"). It is a template variable in its own
            // right too, so an edition that would rather print the abstract than an AI paragraph can.
            .Select(s => new { s.Title, s.Track, s.Abstract, s.EventId })
            .FirstOrDefaultAsync(ct);

        var speakers = await _db.SessionSpeakers
            // §905 — same test-account exclusion as TrackValuesAsync above.
            .Where(ss => ss.SessionId == sessionId && ss.Participant != null
                         && ss.Participant.IsActive && !ss.Participant.IsTestUser)
            .Select(ss => new { ss.ParticipantId, ss.Participant!.FullName })
            .ToListAsync(ct);

        var people = await WithMentionUrnsAsync(
            speakers.Select(s => (s.ParticipantId, s.FullName)).ToList(), session?.EventId, ct);

        var names = people.Select(p => p.FullName).ToList();

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SessionTitle"]    = session?.Title,
            ["SessionAbstract"] = session?.Abstract,
            ["TrackName"]       = session?.Track,
            // §908 — his spelling for the same value (see TrackValuesAsync).
            ["SpeakerTrack"]    = session?.Track,
            ["SpeakerNames"] = names.Count == 0 ? null : string.Join(" | ", names),
            ["Speakers"]     = MentionList(people),
        };
    }

    /// <summary>
    /// §912 — Type 2 for a SPONSOR SPEAKER SESSION, which lives in its own table.
    /// </summary>
    /// <remarks>
    /// <para>§824.1 lists sponsor speaker sessions as a Type 2 subject announced <b>2 ×</b>. They are
    /// NOT <see cref="Domain.Session"/> rows: a sponsor enters the title, abstract and speaker in
    /// their Get-Started wizard and CEH stores it as a <see cref="Domain.SponsorSession"/> for later
    /// one-way push to the Backstage agenda (§292). <b>The planner reads <c>Sessions</c>, so it has
    /// never seen them at all</b> — that is why this line of §824.1 was unbuilt rather than merely
    /// mis-scheduled.</para>
    ///
    /// <para>🔒 The same variable names as an ordinary session, deliberately: the Type 2 template and
    /// his 38 wordings must render a sponsor's session without knowing it came from a different
    /// table.</para>
    /// </remarks>
    public async Task<Dictionary<string, string?>> SponsorSessionValuesAsync(
        int eventId, int sponsorSessionId, CancellationToken ct = default)
    {
        var s = await _db.SponsorSessions
            .Where(x => x.Id == sponsorSessionId && x.EventId == eventId)
            .Select(x => new { x.Title, x.Abstract, x.Track })
            .FirstOrDefaultAsync(ct);

        if (s is null) return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // ⚠️ Same exclusions as a normal session's speakers (§905/§909): active, real people only.
        var speakers = await _db.SponsorSessionSpeakers
            .Where(x => x.SponsorSessionId == sponsorSessionId
                        && x.ParticipantId != null
                        && x.Participant!.IsActive && !x.Participant.IsTestUser)
            .Select(x => new { ParticipantId = x.ParticipantId!.Value, x.Participant!.FullName })
            .ToListAsync(ct);

        var people = await WithMentionUrnsAsync(
            speakers.Select(x => (x.ParticipantId, x.FullName)).ToList(), eventId, ct);
        var names = people.Select(p => p.FullName).ToList();

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SessionTitle"]    = s.Title,
            ["SessionAbstract"] = s.Abstract,
            ["TrackName"]       = s.Track,
            ["SpeakerTrack"]    = s.Track,
            ["SpeakerNames"] = names.Count == 0 ? null : string.Join(" | ", names),
            ["Speakers"]     = MentionList(people),
        };
    }

    /// <summary>
    /// §834.4 — Type 5, one imported EVENT POST's values, keyed by its slug (§828).
    /// </summary>
    /// <remarks>
    /// 🔒 <c>{EventPostBody}</c> is <b>his own finished copy</b>, imported from the deck and owned by
    /// CEH from that moment (§828). Unlike the other four types there is nothing to generate: the
    /// post already exists, and the template's only job is to put a footer under it.
    /// </remarks>
    public async Task<Dictionary<string, string?>> EventPostValuesAsync(
        int eventId, string slug, CancellationToken ct = default)
    {
        var post = await _db.EventSoMePosts
            .Where(p => p.EventId == eventId && p.Slug == slug)
            .Select(p => new { p.Title, p.Body })
            .FirstOrDefaultAsync(ct);

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["EventPostTitle"] = post?.Title,
            ["EventPostBody"]  = post?.Body,
        };
    }

    /// <summary>Type 4 — one sponsor company's values.</summary>
    public async Task<Dictionary<string, string?>> SponsorValuesAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default)
    {
        var s = await _db.SponsorInfos
            .Where(x => x.EventId == eventId && x.SponsorCompanyId == sponsorCompanyId)
            .Select(x => new
            {
                x.CompanyName, x.SponsorPackage, x.SocialMediaIntro, x.WebsiteUrl,
                x.LinkedInUrl, x.LinkedInOrganizationId,
            })
            .FirstOrDefaultAsync(ct);

        if (s is null) return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // §824.16 — the DB-local mirrored name. The full public→legal→billing chain (DESIGN §6) lives
        // in Company Manager; a background post composer must not depend on a third-party API being
        // up, so the locally mirrored name is used and the id is the honest last resort.
        var name = SponsorCompanyName.Resolve(s.CompanyName, null, null, sponsorCompanyId);

        // §884.3 — the company's PEOPLE, mentioned where LinkedIn allows it. ALL signers and ALL
        // coordinators (operator: *"if more signers or coordinators, mention all"*), so a company
        // with two of either does not silently lose one.
        var contacts = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.IsActive
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId == sponsorCompanyId
                        && (p.IsSigner || p.IsEventCoordinator))
            .Select(p => new { p.FullName, p.IsSigner, p.IsEventCoordinator, p.LinkedInPersonUrn })
            .ToListAsync(ct);

        var signers = contacts.Where(c => c.IsSigner)
            .Select(c => new SpeakerNameAndUrn(c.FullName, c.LinkedInPersonUrn)).ToList();
        var coordinators = contacts.Where(c => c.IsEventCoordinator && !c.IsSigner)
            .Select(c => new SpeakerNameAndUrn(c.FullName, c.LinkedInPersonUrn)).ToList();

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SponsorName"]    = name,
            ["SponsorTier"]    = s.SponsorPackage.ToString(),
            ["SponsorWebsite"] = TrimScheme(s.WebsiteUrl),
            ["SponsorSocialMediaCompanyDescription"] = s.SocialMediaIntro,
            ["SponsorHashtag"] = Hashtag(name),
            ["SponsorLinkedInUrl"] = MentionOrName(name, s.LinkedInOrganizationId, s.LinkedInUrl),
            // 🔒 A person who is BOTH signer and coordinator appears once, under signer — being
            // tagged twice in one post reads as a mistake.
            ["SponsorSigner"] = MentionList(signers),
            ["SponsorEventCoordinators"] = MentionList(coordinators),
        };
    }

    /// <summary>Type 3 — one TIER's values: the tier name and the companies in it.</summary>
    public async Task<Dictionary<string, string?>> SponsorTierValuesAsync(
        int eventId, SponsorPackage tier, CancellationToken ct = default)
    {
        var rows = await _db.SponsorInfos
            .Where(x => x.EventId == eventId && x.SponsorPackage == tier)
            .Select(x => new
            {
                x.SponsorCompanyId, x.CompanyName, x.LinkedInOrganizationId, x.LinkedInUrl,
            })
            .ToListAsync(ct);

        // 🔴 §905 — a TEST company is not named in a tier announcement. Operator 2026-08-06:
        // *"sponsor categories and sponsor individual of type test should not be included"*.
        var testCompanies = await TestDataScope.TestSponsorCompanyIdsAsync(_db, eventId, ct);

        var listed = rows
            .Where(r => r.SponsorCompanyId == null || !testCompanies.Contains(r.SponsorCompanyId))
            .Select(r => MentionOrName(
                SponsorCompanyName.Resolve(r.CompanyName, null, null, r.SponsorCompanyId),
                r.LinkedInOrganizationId, r.LinkedInUrl))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SponsorTier"] = tier.ToString(),
            ["SponsorList"] = listed.Count == 0 ? null : string.Join(" | ", listed),
        };
    }

    /// <summary>
    /// §824.12c — a real LinkedIn MENTION when we hold the organization id, else the plain name.
    /// </summary>
    /// <remarks>
    /// <para>The mention form is LinkedIn's inline annotation, <c>@[Name](urn:li:organization:{id})</c>.
    /// It is only emitted from an id we actually hold: §824.14c measured that the lookup which would
    /// derive one is refused for this app, so there is no "resolve it at post time" path.</para>
    ///
    /// <para>🔒 <b>The fallback is the sponsor's NAME, never a raw URL and never an empty gap.</b>
    /// That is exactly how the ELDK26 posts read ("announce Truesec as a Gold sponsor"), so an
    /// untagged sponsor produces the post he has already published dozens of times — not a visibly
    /// degraded one.</para>
    /// </remarks>
    internal static string MentionOrName(string name, string? organizationId, string? linkedInUrl)
    {
        var urn = LinkedInUrlParser.TryBuildOrganizationUrn(organizationId, linkedInUrl);
        return urn is null ? name : $"@[{name}]({urn})";
    }

    /// <summary>"Truesec" → "#Truesec", as the Type 4 sample leads its tag block.</summary>
    internal static string? Hashtag(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cleaned = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return cleaned.Length == 0 ? null : "#" + cleaned;
    }

    /// <summary>"https://truesec.com/" → "Truesec.com" — the sample prints a domain, not a URL.</summary>
    internal static string? TrimScheme(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var s = url.Trim();
        foreach (var p in new[] { "https://", "http://" })
        {
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { s = s[p.Length..]; break; }
        }
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        return s.TrimEnd('/');
    }

    /// <summary>
    /// "24+25th February 2026" — the sample's own phrasing, which reads as a person wrote it.
    /// </summary>
    /// <remarks>
    /// Invariant culture on purpose: the post is written in English regardless of the server's
    /// locale, and a Danish month name appearing in an English sentence is the kind of detail that
    /// makes an automated post look automated.
    /// </remarks>
    internal static string FormatDates(DateOnly start, DateOnly end)
    {
        var ci = CultureInfo.InvariantCulture;
        if (start == end) return start.ToString("d MMMM yyyy", ci);

        // 🔑 §904 — AN EN DASH, because that is what he writes. This branch used to render "9+10
        // February 2027". Nobody writes a date range with a plus, the cross-month branch below
        // already used a dash, and the deck he wrote by hand says "9–10 February 2027" in all 21
        // posts that carry the dates. It only became visible when those posts were tokenised and
        // {EventDates} started supplying the words he had written himself.
        return start.Month == end.Month && start.Year == end.Year
            ? $"{start.Day}–{end.Day} {start.ToString("MMMM yyyy", ci)}"
            : $"{start.ToString("d MMMM", ci)} - {end.ToString("d MMMM yyyy", ci)}";
    }
}
