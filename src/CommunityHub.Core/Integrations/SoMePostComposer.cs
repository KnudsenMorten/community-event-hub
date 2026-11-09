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
    /// <summary>
    /// §888.4 — the edition-wide tokens EVERY post type can use. Shared rather than repeated five
    /// times: a new variable was added in three of the five lists and missing from the others, which
    /// is exactly how the operator ended up looking at a chip row with half the variables on it
    /// (2026-08-06: *"i am missing many variables here — both sponsors + the new ones"*).
    /// </summary>
    private static readonly string[] EditionTokens =
    [
        "{EventSystemUrl}", "{EventTags}", "{EventDates}", "{EventVenue}",
        "{EventVenueCityCountry}", "{EventNameShort}", "{EventNameLong}",
        "{Action_catalog_random}", "{Organizers}",
    ];

    public static IReadOnlyList<string> TokensFor(SoMeTemplateKind? kind) =>
    [
        .. kind switch
        {
            // 🔒 §908.5 — ONE NAME PER VALUE IS WHAT THE EDITOR OFFERS. His catalog originally wrote
            // {SpeakerTrack} and {SessionTeaserTextAI}; those bodies were normalised to the names
            // CEH already resolves, so the chips teach a single spelling rather than presenting two
            // that do the same thing (§858.4b: "no need to have 2 or 3 similar").
            // ⚠️ The old spellings still RESOLVE (see ValuesForAsync / TrackValuesAsync) so a body
            // pasted with them cannot publish a literal token — they are compatibility, not choice.
            SoMeTemplateKind.SpeakerTracks =>
                (string[])["{TrackName}", "{SpeakerNames}", "{Speakers}"],
            SoMeTemplateKind.Session =>
                ["{SessionTitle}", "{SessionAbstract}", "{TrackName}", "{SpeakerNames}", "{Speakers}"],
            SoMeTemplateKind.SponsorCategory =>
                ["{SponsorTier}", "{SponsorList}", "{SponsorNames}"],
            SoMeTemplateKind.Sponsor =>
                ["{SponsorName}", "{SponsorTier}", "{SponsorLinkedInUrl}", "{SponsorWebsite}",
                 "{SponsorSocialMediaCompanyDescription}", "{SponsorHashtag}",
                 "{SponsorCompanyMention}", "{SponsorSigner}", "{SponsorEventCoordinators}"],
            _ => [],
        },
        .. EditionTokens,
    ];

    /// <summary>Every value this post's tokens can draw on, resolved NOW.</summary>
    public async Task<Dictionary<string, string?>> ValuesForAsync(
        SoMePost post, CancellationToken ct = default)
    {
        var values = await _variables.EditionValuesAsync(post.EventId, ct);
        AddPostValues(values, post);
        Merge(values, await SubjectValuesAsync(post, ct));
        ResolveIntroTokens(values);
        return values;
    }

    /// <summary>
    /// §1373 — the stored intro may itself carry tokens (a tier intro carries <c>{SponsorNames}</c>),
    /// resolved here, one level, against the same values — so its names are as live as the body's.
    /// </summary>
    private static void ResolveIntroTokens(Dictionary<string, string?> values)
    {
        foreach (var key in new[] { "IntroText", "SessionTeaserTextAI" })
        {
            if (values.GetValueOrDefault(key) is { } intro && intro.Contains('{'))
            {
                values[key] = SoMeTemplateRenderer.Render(intro, values);
            }
        }
    }

    /// <summary>
    /// §1371 — the RESOLVED text of many posts at once, for a list (the queue's Text column).
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-10-27: <i>"why does it show the template here and not real text"</i>. The
    /// queue printed the stored body, which since §901 is the TEMPLATE — so a row read
    /// <c>{SessionTitle} {IntroText} With {Speakers}</c>. Same fix as §1028 on the sponsor's page.</para>
    /// <para>🔑 Same values as <see cref="ValuesForAsync"/>, but the edition values are read once and
    /// each subject's values once: the queue lists the whole edition, and per post that is a few
    /// hundred posts × several queries.</para>
    /// <para>🔒 A post whose values cannot be read falls back to its raw body, never to nothing.</para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, string>> ResolvedTextsAsync(
        IReadOnlyCollection<SoMePost> posts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(posts);

        var result = new Dictionary<int, string>(posts.Count);
        var editions = new Dictionary<int, Dictionary<string, string?>>();
        var subjects = new Dictionary<(int, SoMeTemplateKind?, string?), Dictionary<string, string?>>();

        foreach (var post in posts)
        {
            try
            {
                if (!editions.TryGetValue(post.EventId, out var edition))
                {
                    editions[post.EventId] = edition = await _variables.EditionValuesAsync(post.EventId, ct);
                }

                var subjectKey = (post.EventId, post.TemplateKind, post.SubjectKey);
                if (!subjects.TryGetValue(subjectKey, out var subject))
                {
                    subjects[subjectKey] = subject = await SubjectValuesAsync(post, ct);
                }

                var values = new Dictionary<string, string?>(edition, edition.Comparer);
                AddPostValues(values, post);
                Merge(values, subject);
                ResolveIntroTokens(values);
                result[post.Id] = Resolve(post.EffectiveText, values);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                result[post.Id] = post.EffectiveText ?? string.Empty;
            }
        }

        return result;
    }

    /// <summary>The values stored on the post row itself.</summary>
    private static void AddPostValues(Dictionary<string, string?> values, SoMePost post)
    {
        // 🔑 His spelling wins. He asked for {Organizers}; the catalog's own token is
        // {OrganizerLinkedInUrls}. Both resolve to the same value rather than becoming two
        // mechanisms that drift apart (§858.4b — "no need to have 2 or 3 similar").
        if (values.TryGetValue("OrganizerLinkedInUrls", out var credits))
        {
            values["Organizers"] = credits;
        }

        // §885 — this post's own call-to-action phrase, drawn ONCE when the post was created and
        // stored on the row. 🔒 Read here, never re-rolled: composing must be repeatable, or the
        // preview stops matching the published post (§863.4). Null ⇒ §824.15 drops the line, so an
        // empty catalog publishes no dangling label.
        values["Action_catalog_random"] = post.ActionPhrase;

        // 🔴 §901 — the AI opening line, written ONCE when the post was planned and read back here.
        // This is what lets the planner store the TEMPLATE instead of the rendering: {IntroText} is
        // the one token that cannot resolve late (it is a generative call), so without this line a
        // stored template would publish the literal text "{IntroText}" at the top of the post.
        // Null ⇒ empty ⇒ the renderer tidies the line away (§824.2D's documented failure path).
        values["IntroText"] = post.IntroText;

        // §908 — HIS name for the same paragraph, from the session wordings he wrote:
        // {SessionTeaserTextAI}. It says out loud what {IntroText} only implies — that this line is
        // machine-written — which is worth keeping in the token he reads in the editor. 🔒 One
        // value, two spellings; never two mechanisms (§858.4b).
        values["SessionTeaserTextAI"] = post.IntroText;
    }

    /// <summary>The values of the post's SUBJECT (its track, session, tier, sponsor or deck run).</summary>
    private async Task<Dictionary<string, string?>> SubjectValuesAsync(SoMePost post, CancellationToken ct)
    {
        var key = post.SubjectKey ?? string.Empty;
        var id = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;

        return post.TemplateKind switch
        {
            SoMeTemplateKind.SpeakerTracks when id.Length > 0 =>
                await _variables.TrackValuesAsync(post.EventId, id, ct),
            // 🔒 §912 — prefix first, exactly as the planner does. Two id spaces, one shaped key.
            SoMeTemplateKind.Session
                when SoMeSponsorSessionKey.TryParse(post.SubjectKey, out var sponsorSessionId) =>
                await _variables.SponsorSessionValuesAsync(post.EventId, sponsorSessionId, ct),
            SoMeTemplateKind.Session when int.TryParse(id, out var sessionId) =>
                await _variables.SessionValuesAsync(sessionId, ct),
            SoMeTemplateKind.SponsorCategory when Enum.TryParse<SponsorPackage>(id, out var tier) =>
                await _variables.SponsorTierValuesAsync(post.EventId, tier, ct),
            SoMeTemplateKind.Sponsor when id.Length > 0 =>
                await _variables.SponsorValuesAsync(post.EventId, id, ct),
            SoMeTemplateKind.EventPost when id.Length > 0 =>
                await _variables.EventPostValuesAsync(post.EventId, id, ct),
            _ => new Dictionary<string, string?>(),
        };
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
