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
            .Select(s => new { s.EventSystemUrl, s.EventTags, s.OrganizerCredits })
            .FirstOrDefaultAsync(ct);

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["EditionCode"]            = ev?.Code,
            ["EventDisplayName"]       = ev?.DisplayName,
            ["EventVenue"]             = ev?.VenueName,
            ["EventDates"]             = ev is null ? null : FormatDates(ev.StartDate, ev.EndDate),
            ["EventSystemUrl"]         = settings?.EventSystemUrl,
            ["EventTags"]              = settings?.EventTags,
            ["OrganizerLinkedInUrls"]  = settings?.OrganizerCredits,
        };
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
        var names = await _db.SessionSpeakers
            .Where(ss => ss.Session!.EventId == eventId
                         && ss.Session.Track == track
                         && !ss.Session.IsServiceSession
                         && ss.Participant != null
                         && ss.Participant.IsActive)
            .Select(ss => ss.Participant!.FullName)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TrackName"]    = track,
            // Empty rather than "no speakers yet": the line simply does not print, and the post is
            // still a correct track announcement.
            ["SpeakerNames"] = names.Count == 0 ? null : string.Join(" | ", names),
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
            .Select(s => new { s.Title, s.Track, s.Abstract })
            .FirstOrDefaultAsync(ct);

        var names = await _db.SessionSpeakers
            .Where(ss => ss.SessionId == sessionId && ss.Participant != null && ss.Participant.IsActive)
            .Select(ss => ss.Participant!.FullName)
            .ToListAsync(ct);

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SessionTitle"]    = session?.Title,
            ["SessionAbstract"] = session?.Abstract,
            ["TrackName"]       = session?.Track,
            ["SpeakerNames"] = names.Count == 0 ? null : string.Join(" | ", names),
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

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SponsorName"]    = name,
            ["SponsorTier"]    = s.SponsorPackage.ToString(),
            ["SponsorWebsite"] = TrimScheme(s.WebsiteUrl),
            ["SponsorSocialMediaCompanyDescription"] = s.SocialMediaIntro,
            ["SponsorHashtag"] = Hashtag(name),
            ["SponsorLinkedInUrl"] = MentionOrName(name, s.LinkedInOrganizationId, s.LinkedInUrl),
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

        var listed = rows
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

        return start.Month == end.Month && start.Year == end.Year
            ? $"{start.Day}+{end.Day} {start.ToString("MMMM yyyy", ci)}"
            : $"{start.ToString("d MMMM", ci)} - {end.ToString("d MMMM yyyy", ci)}";
    }
}
