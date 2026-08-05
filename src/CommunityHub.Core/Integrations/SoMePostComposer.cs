using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §864/§865.2(5) — RESOLVE A POST'S VARIABLES, FOR PUBLISHING AND FOR THE COLOURED PREVIEW.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-05: <i>"i want the preview functionality to embed the variables i want to
/// use in the freestyle editor … other tools supports variables in the freestyle editor - can you
/// support that as well ? like {Speakers} {Organizers} {SponsorTier}"</i>.</para>
///
/// <para>🔑 <b>He types the token where he wants it.</b> That is better than the §861 shape I built
/// first, which appended the organizer credit in a fixed place and gave him no say. A token in his
/// own copy keeps BOTH properties: he controls the position, and the value stays dynamic.</para>
///
/// <para>🔒 <b>RESOLVED AT PUBLISH, NEVER STORED RESOLVED</b> (§864) — that is the whole point. A
/// sponsor who fixes their social text the day before publication, or a second speaker linked to a
/// session, reaches a post planned months earlier. Storing the resolved value would freeze it, which
/// is exactly the defect §861 removed from the credit.</para>
///
/// <para>⚠️ <b>An UNRESOLVED token must never stop a post publishing.</b> At compose time an unknown
/// token is refused (§858.4) — correct there, dangerous here, where refusing means a post silently
/// does not go out. The renderer already preserves unknown tokens verbatim; the publisher REPORTS
/// them instead of failing (§864.3).</para>
/// </remarks>
public sealed class SoMePostComposer
{
    private readonly CommunityHubDbContext _db;
    private readonly SoMeVariableResolver _variables;

    public SoMePostComposer(CommunityHubDbContext db, SoMeVariableResolver variables)
    {
        _db = db;
        _variables = variables;
    }

    /// <summary>One run of a post's text: either his own words, or a resolved variable.</summary>
    /// <param name="Text">What the reader sees — the RESOLVED value for a variable.</param>
    /// <param name="Token">The token name when this is a variable, else null.</param>
    /// <param name="Resolved">False when the token had no value — shown, but flagged.</param>
    public readonly record struct Segment(string Text, string? Token, bool Resolved)
    {
        public bool IsVariable => Token is not null;
    }

    /// <summary>
    /// The tokens offered in the editor for a given post type, so he can insert rather than recall.
    /// </summary>
    /// <remarks>
    /// 🔒 Only tokens that CAN resolve for that type are offered — offering <c>{SponsorTier}</c> on
    /// an event post would produce a token that renders as itself forever.
    /// </remarks>
    public static IReadOnlyList<string> TokensFor(SoMeTemplateKind? kind) => kind switch
    {
        SoMeTemplateKind.SpeakerTracks =>
            ["{TrackName}", "{SpeakerNames}", "{Speakers}", "{EventSystemUrl}", "{EventTags}",
             "{EventDates}", "{EventVenue}", "{EditionCode}", "{Organizers}"],
        SoMeTemplateKind.Session =>
            ["{SessionTitle}", "{SessionAbstract}", "{SpeakerNames}", "{Speakers}", "{EventSystemUrl}",
             "{EventTags}", "{EventDates}", "{EventVenue}", "{EditionCode}", "{Organizers}"],
        SoMeTemplateKind.SponsorCategory =>
            ["{SponsorTier}", "{SponsorList}", "{EventSystemUrl}", "{EventTags}", "{EventDates}",
             "{EventVenue}", "{EditionCode}", "{Organizers}"],
        SoMeTemplateKind.Sponsor =>
            ["{SponsorName}", "{SponsorTier}", "{SponsorLinkedInUrl}", "{SponsorWebsite}",
             "{SponsorSocialMediaCompanyDescription}", "{SponsorHashtag}", "{EventSystemUrl}",
             "{EventTags}", "{EditionCode}", "{Organizers}"],
        _ =>
            ["{EventSystemUrl}", "{EventTags}", "{EventDates}", "{EventVenue}", "{EditionCode}",
             "{Organizers}"],
    };

    /// <summary>Every value this post's tokens can draw on, resolved NOW.</summary>
    public async Task<Dictionary<string, string?>> ValuesForAsync(
        SoMePost post, CancellationToken ct = default)
    {
        var values = await _variables.EditionValuesAsync(post.EventId, ct);

        // 🔑 His spelling wins. He asked for {Organizers}; the catalog's own token is
        // {OrganizerLinkedInUrls}. Both resolve to the same value rather than becoming two
        // mechanisms that drift apart (§858.4b — "no need to have 2 or 3 similar").
        if (values.TryGetValue("OrganizerLinkedInUrls", out var credits))
        {
            values["Organizers"] = credits;
        }

        var key = post.SubjectKey ?? string.Empty;
        var id = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;

        switch (post.TemplateKind)
        {
            case SoMeTemplateKind.SpeakerTracks when id.Length > 0:
                Merge(values, await _variables.TrackValuesAsync(post.EventId, id, ct));
                break;
            case SoMeTemplateKind.Session when int.TryParse(id, out var sessionId):
                Merge(values, await _variables.SessionValuesAsync(sessionId, ct));
                break;
            case SoMeTemplateKind.SponsorCategory when Enum.TryParse<SponsorPackage>(id, out var tier):
                Merge(values, await _variables.SponsorTierValuesAsync(post.EventId, tier, ct));
                break;
            case SoMeTemplateKind.Sponsor when id.Length > 0:
                Merge(values, await _variables.SponsorValuesAsync(post.EventId, id, ct));
                break;
            case SoMeTemplateKind.EventPost when id.Length > 0:
                Merge(values, await _variables.EventPostValuesAsync(post.EventId, id, ct));
                break;
        }

        return values;
    }

    /// <summary>The text that publishes: his body with every token it carries resolved.</summary>
    public string Resolve(string? body, IReadOnlyDictionary<string, string?> values) =>
        SoMeTemplateRenderer.Render(body, values);

    /// <summary>
    /// The body split into his words and its variables, so the editor can colour them apart.
    /// </summary>
    /// <remarks>
    /// 🔒 §865.2(5) — he reads the REAL values, but can still see which words are not his:
    /// <i>"preview shows everything right now, but it is built up with variables and i can separate
    /// variables and free-style text apart by colors. i cannot edit a variable text"</i>.
    /// The colour is also what stops him retyping a value into literal text and re-freezing it, the
    /// way the credit froze into posts 383 and 495 (§861.3).
    /// </remarks>
    public static IReadOnlyList<Segment> Segments(
        string? body, IReadOnlyDictionary<string, string?> values)
    {
        var result = new List<Segment>();
        if (string.IsNullOrEmpty(body)) return result;

        var lookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values)
        {
            if (!string.IsNullOrWhiteSpace(k)) lookup[k.Trim().Trim('{', '}')] = v;
        }

        var rx = new System.Text.RegularExpressions.Regex(
            @"\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        var pos = 0;
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(body))
        {
            if (m.Index > pos) result.Add(new Segment(body[pos..m.Index], null, true));

            var name = m.Groups["name"].Value;
            if (lookup.TryGetValue(name, out var val))
            {
                // A known token with no value is still a VARIABLE — it just has nothing today.
                // Showing it as empty-but-flagged is honest; hiding it would be a silent gap.
                result.Add(new Segment(val ?? string.Empty, name, !string.IsNullOrEmpty(val)));
            }
            else
            {
                // Unknown token: kept verbatim so it is impossible to miss, never silently dropped.
                result.Add(new Segment(m.Value, name, false));
            }

            pos = m.Index + m.Length;
        }

        if (pos < body.Length) result.Add(new Segment(body[pos..], null, true));
        return result;
    }

    /// <summary>Tokens in the body that no value could fill — reported, never swallowed (§864.3).</summary>
    public static IReadOnlyList<string> UnresolvedTokens(
        string? body, IReadOnlyDictionary<string, string?> values) =>
        Segments(body, values)
            .Where(s => s.IsVariable && !s.Resolved)
            .Select(s => s.Token!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void Merge(Dictionary<string, string?> into, Dictionary<string, string?> from)
    {
        foreach (var (k, v) in from) into[k] = v;
    }
}
