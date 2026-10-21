using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §824.2C — substitutes <c>{Variable}</c> placeholders in a SoMe post body.
/// </summary>
/// <remarks>
/// <para>Flat token substitution, deliberately: the operator edits these templates himself, and a
/// template language with conditionals is a thing he would have to learn and CEH would have to
/// explain in an error message.</para>
///
/// <para>🔒 <b>An UNKNOWN placeholder is left visible on purpose</b> — the same rule the e-mail
/// renderer follows. A typo'd <c>{SponsorNmae}</c> that silently vanished would produce a post that
/// reads fine to the composer and is missing the sponsor's name to 400+ followers. Left standing, it
/// is caught in the preview by anyone glancing at it.</para>
///
/// <para>⚠️ <b>A KNOWN placeholder with no value becomes EMPTY, which is different.</b> A sponsor
/// with no <c>LinkedInOrganizationId</c>, a session with no track — those are ordinary states, not
/// mistakes, and the surrounding sentence has to survive them. That is why
/// <see cref="Render"/> also tidies the whitespace an emptied line leaves behind.</para>
/// </remarks>
public static class SoMeTemplateRenderer
{
    private static readonly Regex Placeholder = new(
        @"\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Render <paramref name="template"/> against <paramref name="values"/> (keys with or without
    /// the braces). Unknown placeholders are preserved verbatim.
    /// </summary>
    public static string Render(string? template, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        ArgumentNullException.ThrowIfNull(values);

        var lookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            lookup[k.Trim().Trim('{', '}')] = v;
        }

        // §1300 — a LABEL line whose placeholders are all known and all empty is dropped, label and
        // all. The templates were written assuming this (§884.3: "a sponsor with no mentionable
        // contact simply has no 'Tag:' line"), but only the substitution existed, so such a sponsor
        // published a bare "Tag:". Kept: a line with any value, any unknown token, no placeholder,
        // or real text of its own ("{SponsorHashtag} #ELDK27", "Learn more at {SponsorWebsite}.").
        var lines = template.Replace("\r\n", "\n").Split('\n')
            .Where(line => !IsEmptiedLabelLine(line, lookup));

        var rendered = SpacedPlaceholder.Replace(string.Join("\n", lines), m =>
        {
            var name = m.Groups["name"].Value;
            var lead = m.Groups["lead"].Value;
            var trail = m.Groups["trail"].Value;
            // 🔒 Only a KNOWN key substitutes. TryGetValue failing means "nobody told us about this
            // token", which is a template bug and must stay visible; a known key holding null means
            // "this post genuinely has no track", which is data and resolves to empty.
            if (!lookup.TryGetValue(name, out var v)) return m.Value;
            if (!string.IsNullOrEmpty(v)) return lead + v + trail;
            // Emptied: take ONE of its surrounding spaces with it, so "Tag: {A} {B}" with A empty
            // reads "Tag: b", not "Tag:  b".
            return lead.Length > 0 && trail.Length > 0 ? lead : string.Empty;
        });

        return TidyBlankLines(rendered);
    }

    private static readonly Regex SpacedPlaceholder = new(
        @"(?<lead>[ \t])?\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}(?<trail>[ \t])?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool IsEmptiedLabelLine(string line, IReadOnlyDictionary<string, string?> lookup)
    {
        var matches = Placeholder.Matches(line);
        if (matches.Count == 0) return false;
        foreach (Match m in matches)
        {
            if (!lookup.TryGetValue(m.Groups["name"].Value, out var v)) return false; // unknown → keep visible
            if (!string.IsNullOrWhiteSpace(v)) return false;
        }
        var rest = Placeholder.Replace(line, string.Empty).Trim();
        return rest.Length == 0 || (rest.EndsWith(':') && rest.Length <= 30 && !rest[..^1].Contains(':'));
    }

    /// <summary>
    /// Collapse the debris an emptied placeholder leaves: trailing spaces, and runs of three or more
    /// newlines down to the blank line the templates use between blocks.
    /// </summary>
    /// <remarks>
    /// Without this, a sponsor with no description publishes a post carrying a three-line gap in the
    /// middle — which reads as something having failed to load, and is the sort of detail that makes
    /// an automated post look automated.
    /// </remarks>
    private static string TidyBlankLines(string s)
    {
        s = Regex.Replace(s, @"[ \t]+(\r?\n)", "$1");
        s = Regex.Replace(s, @"(\r?\n){3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>
    /// The placeholders a template uses that nothing can resolve — what the editor shows as a
    /// warning before he saves.
    /// </summary>
    /// <remarks>
    /// Checked against <see cref="SoMeTemplateCatalog.KnownVariables"/> rather than against the
    /// values of one particular post, so an empty-for-this-sponsor value never masquerades as a typo.
    /// </remarks>
    public static IReadOnlyList<string> UnknownPlaceholders(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return Array.Empty<string>();

        var known = SoMeTemplateCatalog.KnownVariables
            .Select(v => v.Trim('{', '}'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Placeholder.Matches(template)
            .Select(m => m.Groups["name"].Value)
            .Where(n => !known.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => $"{{{n}}}")
            .ToList();
    }
}
