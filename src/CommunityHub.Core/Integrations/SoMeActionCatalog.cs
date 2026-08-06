namespace CommunityHub.Core.Integrations;

/// <summary>
/// §885 — the prestaged call-to-action phrases behind <c>{Action_catalog_random}</c>: one per line,
/// one drawn per post so a campaign of 80+ posts does not close the same way every time.
///
/// <para>🔒 <b>Rolling happens ONCE, when the post is created</b>, and the phrase is stored on the
/// post. This class only parses and picks; it deliberately has no notion of "re-render", because a
/// phrase that changed on every render would make the preview disagree with what publishes — the
/// defect §863.4 exists to prevent.</para>
/// </summary>
public static class SoMeActionCatalog
{
    /// <summary>
    /// §326k escapes these as little-text-format control characters, so a phrase containing one
    /// would publish with visible backslashes. Validated on SAVE rather than discovered in a post.
    /// </summary>
    private static readonly char[] Forbidden = { '@', '[', ']', '(', ')', '{', '}', '*', '_', '~', '|', '<', '>', '\\' };

    /// <summary>The usable phrases, in order. Blank lines are ignored so the box can be grouped.</summary>
    public static IReadOnlyList<string> Parse(string? catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog)) return Array.Empty<string>();

        return catalog
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    /// <summary>
    /// The phrases that would publish badly, with the character that breaks each — so the settings
    /// page can refuse them by name (§854: never fail quietly, and never fail vaguely).
    /// Emoji are fine and deliberately NOT restricted.
    /// </summary>
    public static IReadOnlyList<(string Phrase, char Character)> Invalid(string? catalog) =>
        Parse(catalog)
            .Select(p => (Phrase: p, Index: p.IndexOfAny(Forbidden)))
            .Where(x => x.Index >= 0)
            .Select(x => (x.Phrase, x.Phrase[x.Index]))
            .ToList();

    /// <summary>
    /// Draw one phrase. <paramref name="random"/> is injected so a test is deterministic and so a
    /// caller could seed per-post if it ever wants reproducibility.
    /// <para>Returns null for an empty catalog — §824.15's renderer then DROPS the line, so a post
    /// never publishes a dangling label or a literal <c>{Action_catalog_random}</c>.</para>
    /// </summary>
    public static string? Pick(string? catalog, Random? random = null)
    {
        var phrases = Parse(catalog);
        if (phrases.Count == 0) return null;

        return phrases[(random ?? Random.Shared).Next(phrases.Count)];
    }

    /// <summary>
    /// Draw a phrase that is not <paramref name="previous"/>, so the same sentence does not land on
    /// two posts in a row. Falls back to the only phrase when the catalog has just one — a one-item
    /// catalog is a fixed line, not an error.
    /// </summary>
    public static string? PickDifferentFrom(string? catalog, string? previous, Random? random = null)
    {
        var phrases = Parse(catalog);
        if (phrases.Count == 0) return null;
        if (phrases.Count == 1) return phrases[0];

        var choices = phrases
            .Where(p => !string.Equals(p, previous, StringComparison.Ordinal))
            .ToList();

        // Every phrase equals `previous` only if the catalog is all duplicates — then any is fine.
        if (choices.Count == 0) choices = phrases.ToList();

        return choices[(random ?? Random.Shared).Next(choices.Count)];
    }
}
