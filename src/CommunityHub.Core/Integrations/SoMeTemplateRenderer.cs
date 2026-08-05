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

        var rendered = Placeholder.Replace(template, m =>
        {
            var name = m.Groups["name"].Value;
            // 🔒 Only a KNOWN key substitutes. TryGetValue failing means "nobody told us about this
            // token", which is a template bug and must stay visible; a known key holding null means
            // "this post genuinely has no track", which is data and resolves to empty.
            return lookup.TryGetValue(name, out var v) ? (v ?? string.Empty) : m.Value;
        });

        return TidyBlankLines(rendered);
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
