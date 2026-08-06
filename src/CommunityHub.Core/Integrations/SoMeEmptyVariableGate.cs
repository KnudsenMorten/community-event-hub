namespace CommunityHub.Core.Integrations;

/// <summary>
/// 🔴 §922 — A POST CANNOT GO OUT WITH AN EMPTY VARIABLE IN IT. The general rule.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"a post can NOT go out if a variable is empty in the post. that is
/// the blocker. that covers fx missing sponsor some text or session text, agree?"</i> — agreed, and
/// it is a better rule than the per-type checklist it joins, because it is derived from the post's
/// OWN content. A new template with a new variable is covered the day it ships, with nothing to
/// remember.</para>
///
/// <para>🔑 <b>EMPTY IS NOT UNKNOWN, and the difference is the whole design.</b>
/// <list type="bullet">
/// <item><b>Empty</b> — a KNOWN variable with no value today: the sponsor has sent no social text,
/// the AI teaser has not been written. Something is genuinely missing ⇒ <b>block</b>.</item>
/// <item><b>Unknown</b> — a token nothing can resolve, e.g. a typo'd <c>{SponsorNmae}</c>. That is a
/// TEMPLATE BUG. §864.3 deliberately publishes it verbatim rather than silently withholding the
/// post, and that stays: blocking on it would hide a typo for ever behind a post that never goes
/// out, with nobody told why.</item>
/// </list></para>
///
/// <para>⚠️ <b>SOME VARIABLES ARE EMPTY BY DESIGN, and blocking on those would be wrong.</b>
/// §824.15's renderer DROPS a line whose variables all resolve empty, and §884.3 built
/// <c>Tag: {SponsorSigner} {SponsorEventCoordinators}</c> precisely so a sponsor with no mentionable
/// contact simply has no "Tag:" line. Measured 2026-08-06: <b>0 of 14 sponsors have a LinkedIn
/// organisation id</b>, so mention-shaped values are routinely absent and always will be. A strict
/// "every variable" rule would block those posts for ever, waiting for something that is never
/// coming.</para>
///
/// <para>🔒 Hence: <b>required by default, with a short explicit optional list.</b> Default-required
/// is the safe direction — a new variable blocks until someone decides it is optional, rather than
/// slipping out empty because nobody remembered to list it.</para>
/// </remarks>
public static class SoMeEmptyVariableGate
{
    /// <summary>
    /// Variables a post may publish WITHOUT. Everything else is required.
    /// </summary>
    /// <remarks>
    /// 🔒 Deliberately short, and each entry earns its place by being a line the template is built
    /// to drop:
    /// <list type="bullet">
    /// <item><c>SponsorSigner</c> / <c>SponsorEventCoordinators</c> — §884.3: a sponsor with no
    /// mentionable contact has no "Tag:" line. Never true for every sponsor, and never fixable by
    /// chasing them.</item>
    /// <item><c>Action_catalog_random</c> — §885/§824.15: an empty catalog drops the line rather
    /// than publishing a dangling label.</item>
    /// <item><c>SponsorHashtag</c> — a sponsor without one simply is not hashtagged.</item>
    /// <item><c>SessionAbstract</c> — long copy that most templates use as optional context; a
    /// session with no abstract is an ordinary state, not a missing dependency.</item>
    /// </list>
    /// ⚠️ <c>IntroText</c> / <c>SessionTeaserTextAI</c> are deliberately NOT here. They used to be
    /// "optional, the template closes the gap" — but in his own session wordings the teaser IS the
    /// body, and a post without it is a title, a link and some tags (§909.1). The AI integration is
    /// a dependency, exactly as he said.
    /// </remarks>
    public static readonly IReadOnlySet<string> Optional =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SponsorSigner",
            "SponsorEventCoordinators",
            "Action_catalog_random",
            "SponsorHashtag",
        };
    // 🔴 §926 — `SessionAbstract` WAS on this list and should not have been. Operator 2026-08-06:
    // *"master class announcement and sessions has dependency to description. if empty it is not
    // ready"*. I had reasoned "a session with no abstract is an ordinary state"; it is not — it is
    // the source the AI teaser is written FROM, so an empty one does not produce a shorter post, it
    // produces an INVENTED one. See SoMeApprovalGate for the check that catches it even when the
    // body never mentions {SessionAbstract}.

    /// <summary>
    /// The REQUIRED variables in <paramref name="body"/> that have no value — empty means blocked.
    /// </summary>
    /// <remarks>
    /// ⚠️ A token the values dictionary does not know at all is NOT returned here: that is the
    /// "unknown" case above, which §864.3 owns.
    /// </remarks>
    public static IReadOnlyList<string> MissingRequired(
        string? body, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<string>();

        // ⚠️ The emptiness test is made HERE rather than read off Segments' `Resolved` flag, which
        // treats "   " as resolved. A whitespace-only value publishes as a blank space — it is a
        // missing value wearing a character, and the rule is about what the reader sees.
        var lookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values)
        {
            if (!string.IsNullOrWhiteSpace(k)) lookup[k.Trim().Trim('{', '}')] = v;
        }

        return SoMePostComposer.Segments(body, values)
            .Where(s => s.IsVariable)
            .Select(s => s.Token!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // KNOWN-but-empty only. An unknown token is a template bug, not a missing dependency:
            // §864.3 publishes it verbatim so somebody sees the typo.
            .Where(t => lookup.TryGetValue(t, out var v) && string.IsNullOrWhiteSpace(v))
            .Where(t => !Optional.Contains(t))
            .ToList();
    }

    /// <summary>The blocker sentence for a human, or null when nothing is missing.</summary>
    /// <remarks>
    /// 🔑 Names the VARIABLES, because that is what someone can act on: "waiting for the sponsor's
    /// social text" is actionable, "this post is not ready" is not (§850's rule — a refusal carries
    /// its reason).
    /// </remarks>
    public static string? ReasonFor(string? body, IReadOnlyDictionary<string, string?> values)
    {
        var missing = MissingRequired(body, values);
        if (missing.Count == 0) return null;

        var names = string.Join(", ", missing.Select(Describe));
        return missing.Count == 1
            ? $"waiting for {names}"
            : $"waiting for {missing.Count} missing values — {names}";
    }

    /// <summary>The variable, in words someone outside the code would recognise.</summary>
    private static string Describe(string token) => token switch
    {
        "IntroText" or "SessionTeaserTextAI" => "the session teaser (written by the AI assistant)",
        "SponsorSocialMediaCompanyDescription" => "the sponsor's own social-media text",
        "SponsorList" => "the companies in this tier",
        "Speakers" or "SpeakerNames" => "the speaker list",
        "SessionTitle" => "the session title",
        "SponsorName" => "the sponsor's name",
        "SponsorWebsite" => "the sponsor's website",
        "SponsorLinkedInUrl" => "the sponsor's LinkedIn page",
        "TrackName" or "SpeakerTrack" => "the track name",
        "EventTags" => "the event hashtags",
        "EventSystemUrl" => "the event link",
        "OrganizerLinkedInUrls" or "Organizers" => "the organizer credit",
        _ => $"{{{token}}}",
    };
}
