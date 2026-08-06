using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §927 — SESSIONS HE DOES NOT WANT ANNOUNCED, matched by TITLE PATTERN.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"exclude option with title filters must be build like ask the
/// experts*"</i>.</para>
///
/// <para>🔑 <b>This is not the heuristic §909 refused, and the difference is who wrote the
/// pattern.</b> §909 declined to detect test sessions by looking for "Test" in the title, because
/// "Penetration Testing" is a real talk and a rule nobody chose would eventually delete it. Here
/// HE chooses the patterns and can see them listed — a setting, not an inference. A wrong exclusion
/// is visible in one place and removed in one edit.</para>
///
/// <para>🔒 <b>An empty pattern list excludes NOTHING.</b> The safe direction: a blank setting must
/// never mean "match everything", which is what a naive empty-pattern-matches-all would do.</para>
/// </remarks>
public static class SoMeTitleExclusions
{
    /// <summary>The usable patterns, one per line. Blank lines and stray spacing are ignored.</summary>
    public static IReadOnlyList<string> Parse(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns)) return Array.Empty<string>();

        return patterns
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="title"/> matches any pattern.
    /// </summary>
    /// <remarks>
    /// <para><c>*</c> is the only wildcard and means "any run of characters", so his
    /// <c>ask the experts*</c> matches "Ask the Experts — Identity". Matching is
    /// case-insensitive and anchored, so <c>ask the experts</c> with no star matches ONLY that
    /// exact title — a pattern that silently behaved as "contains" would exclude more than it
    /// looks like it does.</para>
    /// <para>⚠️ Everything else is escaped, so a title pattern containing <c>(</c> or <c>.</c> —
    /// "AI for Makers (Copilot &amp; Agents)" — is matched literally rather than being read as
    /// regular-expression syntax.</para>
    /// </remarks>
    public static bool IsExcluded(string? title, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrWhiteSpace(title) || patterns.Count == 0) return false;

        foreach (var pattern in patterns)
        {
            if (Regex.IsMatch(title.Trim(), ToRegex(pattern), RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Convenience: parse and match in one call.</summary>
    public static bool IsExcluded(string? title, string? patterns) =>
        IsExcluded(title, Parse(patterns));

    /// <summary>A wildcard pattern as an anchored regular expression.</summary>
    private static string ToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*");
        return $"^{escaped}$";
    }
}
