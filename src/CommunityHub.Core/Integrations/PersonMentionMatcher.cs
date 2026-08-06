using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>§858.16 — why a speaker could not be mentioned. Never "just null".</summary>
public enum MentionResolutionStatus
{
    /// <summary>A single follower matched — a real <c>urn:li:person:</c> mention is possible.</summary>
    Resolved,

    /// <summary>The search ran and returned nobody matching. They do not follow the page (§858.16b).</summary>
    NotAFollower,

    /// <summary>Several followers matched the name and nothing distinguishes them (§858.16f).</summary>
    Ambiguous,

    /// <summary>No usable search term could be built from the stored name.</summary>
    KeywordUnusable,

    /// <summary>The lookup itself failed (HTTP/throttle). 🔒 NOT the same as "not a follower" (§858.16h).</summary>
    LookupFailed,
}

/// <summary>The outcome of resolving one person, with a reason fit to show the organizer.</summary>
public sealed record MentionResolution(
    string? Urn,
    MentionResolutionStatus Status,
    string Reason)
{
    public bool IsResolved => Status == MentionResolutionStatus.Resolved && !string.IsNullOrEmpty(Urn);

    public static MentionResolution Ok(string urn, string matchedName) =>
        new(urn, MentionResolutionStatus.Resolved, $"matched follower '{matchedName}'");
}

/// <summary>One candidate as LinkedIn's peopleTypeahead returns it.</summary>
public sealed record TypeaheadCandidate(string Member, string FirstName, string LastName)
{
    public string DisplayName => $"{FirstName} {LastName}".Trim();
}

/// <summary>
/// §858.16 — the PURE half of speaker→mention resolution: build a search term LinkedIn will
/// accept, and pick the right person out of the candidates it returns.
///
/// <para>Every rule here was measured against the live API on 2026-08-06, not read from docs:</para>
/// <list type="bullet">
///   <item><b>At most ONE space</b> in <c>keywords</c> — three-part names are rejected with
///     <c>400 INVALID_SPACE_CHARACTER_COUNT_IN_SEARCH_KEYWORD</c> (§858.16h). This cost us two
///     speakers who were wrongly written off as non-followers.</item>
///   <item><b>Letters, space, apostrophe, hyphen and dot ONLY</b> — <c>æ ø å</c> return
///     <c>400 NOT_ALLOWED_CHARACTERS_IN_SEARCH_KEYWORD</c>. Strip the diacritic (ø→o); do NOT
///     expand it (oe), which matched nobody (§858.16e).</item>
///   <item><b>Matching is prefix, on name parts</b>, and LinkedIn folds diacritics on its own
///     side — so we only have to make the term sendable.</item>
///   <item><b>A name is not unique</b> — "Kasper" returned 7 followers. Never take the first
///     candidate; an ambiguous answer must be reported, not guessed (§858.16f).</item>
/// </list>
/// </summary>
public static class PersonMentionMatcher
{
    // LinkedIn's accepted keyword charset, measured. Everything else must go.
    private static readonly Regex Disallowed = new(@"[^A-Za-z '\.\-]", RegexOptions.Compiled);

    // Decorations real speakers carry on LinkedIn: "[MVP]", "(she/her)", emoji, flags.
    private static readonly Regex Decorations = new(@"[\[\(\{][^\]\)\}]*[\]\)\}]", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Fold to LinkedIn's sendable alphabet: strip diacritics (ø→o, é→e), drop decorations
    /// and any remaining disallowed character, collapse whitespace.
    /// 🔒 Strips the MARK, never expands it — "Noerregaard" matched nobody where "Norregaard" matched.
    /// </summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var withoutDecorations = Decorations.Replace(value, " ");

        // Decompose so a diacritic becomes a separate combining mark we can drop.
        var decomposed = withoutDecorations.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch);
        }

        // Danish 'ø'/'Ø' carry a STROKE, not a combining mark, so decomposition leaves them intact.
        var stroked = sb.ToString().Replace('ø', 'o').Replace('Ø', 'O')
                                   .Replace("æ", "a").Replace("Æ", "A")
                                   .Replace("ß", "ss");

        return Whitespace.Replace(Disallowed.Replace(stroked, " "), " ").Trim();
    }

    /// <summary>
    /// The search term to send. 🔒 <b>ONE name part</b> — the surname when we have one, because a
    /// surname is far more selective than a given name ("Jan" returns nine followers, "Elven" one).
    /// Returns null when nothing sendable survives folding.
    /// </summary>
    public static string? BuildKeyword(string? firstName, string? lastName)
    {
        foreach (var source in new[] { lastName, firstName })
        {
            var folded = Fold(source);
            if (folded.Length == 0) continue;

            // A stored "last name" may itself be multi-part ("Waltorp Knudsen"); the final token
            // is the surname proper and keeps us inside the one-space rule.
            var token = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(token) && token.Length >= 2) return token;
        }

        return null;
    }

    /// <summary>
    /// §858.17 — the <c>/in/{slug}</c> from a stored LinkedIn profile URL, lower-cased and
    /// percent-decoded. This is the ONLY thing that can tell two identically-named followers apart:
    /// Typeahead returns no slug, but a candidate's URN resolves to one (§858.1), so the stored slug
    /// is the tie-break. Null when the URL is absent or is not a personal profile.
    /// </summary>
    public static string? TryReadPersonSlug(string? profileUrl)
    {
        if (string.IsNullOrWhiteSpace(profileUrl)) return null;

        var m = PersonProfileUrl.Match(profileUrl);
        if (!m.Success) return null;

        var slug = Uri.UnescapeDataString(m.Groups["slug"].Value).Trim().TrimEnd('/');
        return slug.Length == 0 ? null : slug.ToLowerInvariant();
    }

    // Any host (linkedin.com, se.linkedin.com, dk.linkedin.com …), with or without scheme/www.
    private static readonly Regex PersonProfileUrl = new(
        @"linkedin\.com/in/(?<slug>[^/?#\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The candidates that plausibly ARE this person, by the name rule below. Exposed so a caller
    /// holding an ambiguous result can disambiguate them (§858.17) rather than give up.
    /// </summary>
    public static IReadOnlyList<TypeaheadCandidate> Matching(
        IReadOnlyList<TypeaheadCandidate> candidates, string? firstName, string? lastName)
    {
        var wantedFirst = FirstToken(Fold(firstName));
        var wantedLast = LastToken(Fold(lastName));
        if (wantedFirst.Length == 0 && wantedLast.Length == 0) return Array.Empty<TypeaheadCandidate>();
        return candidates.Where(c => Matches(c, wantedFirst, wantedLast)).ToList();
    }

    /// <summary>
    /// Pick the one follower who is our speaker. Requires the given name to line up AND our
    /// surname to appear among theirs — which tolerates the very common case of LinkedIn holding
    /// extra middle names or a suffix ("Morten Knudsen" vs "Morten Waltorp Knudsen [MVP]")
    /// without collapsing into "any Morten will do".
    /// <para>🔒 0 matches ⇒ NotAFollower · 2+ ⇒ Ambiguous. Never <c>elements[0]</c>.</para>
    /// </summary>
    public static MentionResolution SelectMatch(
        IReadOnlyList<TypeaheadCandidate> candidates, string? firstName, string? lastName)
    {
        var wantedFirst = FirstToken(Fold(firstName));
        var wantedLast = LastToken(Fold(lastName));

        if (wantedFirst.Length == 0 && wantedLast.Length == 0)
        {
            return new MentionResolution(null, MentionResolutionStatus.KeywordUnusable,
                "the stored name has nothing searchable in it");
        }

        var matches = candidates.Where(c => Matches(c, wantedFirst, wantedLast)).ToList();

        return matches.Count switch
        {
            1 => MentionResolution.Ok(matches[0].Member, matches[0].DisplayName),

            0 => new MentionResolution(null, MentionResolutionStatus.NotAFollower,
                    candidates.Count == 0
                        ? "nobody with that name follows the page"
                        : $"{candidates.Count} follower(s) share the name but none matched "
                          + $"'{firstName} {lastName}'".Trim()),

            _ => new MentionResolution(null, MentionResolutionStatus.Ambiguous,
                    $"{matches.Count} followers match '{firstName} {lastName}' "
                    + $"({string.Join(", ", matches.Take(3).Select(m => m.DisplayName))}) "
                    + "— cannot tell which is the speaker"),
        };
    }

    private static bool Matches(TypeaheadCandidate candidate, string wantedFirst, string wantedLast)
    {
        var candFirst = FirstToken(Fold(candidate.FirstName));
        var candLastTokens = Fold(candidate.LastName)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // The surname must appear somewhere in theirs (handles middle names + suffixes).
        var surnameOk = wantedLast.Length == 0 ||
                        candLastTokens.Any(t => t.Equals(wantedLast, StringComparison.OrdinalIgnoreCase));

        // The given name must line up. Prefix-tolerant so "Nikki"/"Nikkita" style shortenings
        // still connect, but never so loose that a different person qualifies.
        var givenOk = wantedFirst.Length == 0 ||
                      candFirst.Equals(wantedFirst, StringComparison.OrdinalIgnoreCase) ||
                      candFirst.StartsWith(wantedFirst, StringComparison.OrdinalIgnoreCase) ||
                      wantedFirst.StartsWith(candFirst, StringComparison.OrdinalIgnoreCase);

        return surnameOk && givenOk;
    }

    private static string FirstToken(string folded) =>
        folded.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

    private static string LastToken(string folded) =>
        folded.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

    /// <summary>
    /// Render a resolved mention in LinkedIn "little text format". The display name is the
    /// organizer's own copy, not LinkedIn's — he writes "Morten Knudsen", not
    /// "Morten Waltorp Knudsen [MVP]".
    /// 🔒 Must survive <c>EscapeCommentaryPreservingMentions</c> (§858.13d) — brackets in the
    /// display name would break the span, so they are stripped.
    /// </summary>
    public static string RenderMention(string displayName, string personUrn)
    {
        // Remove a decoration WHOLE ("[MVP]" → nothing). Stripping only the brackets would leave
        // "Thomas Marcussen MVP" in the published post, which reads as a mistake.
        var safe = Decorations.Replace(displayName, " ");
        safe = safe.Replace("[", string.Empty).Replace("]", string.Empty)
                   .Replace("(", string.Empty).Replace(")", string.Empty);
        return $"@[{Whitespace.Replace(safe, " ").Trim()}]({personUrn})";
    }
}
