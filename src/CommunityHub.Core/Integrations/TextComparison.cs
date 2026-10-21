using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1256d — compare two pieces of human text the way a READER sees them, so a difference nobody
/// can see never becomes hand-work.
///
/// <para><b>The case this exists for.</b> A CEH session title read
/// <c>"Panel:··Security &amp; Compliance"</c> (two spaces) while Zoho held
/// <c>"Panel:·Security &amp; Compliance"</c> (one). An ordinal comparison called that a change, so
/// the hub mailed *"ACTION NEEDED … Title: 'Panel: Security &amp; Compliance' →
/// 'Panel: Security &amp; Compliance'"* — and HTML collapses runs of whitespace when it renders, so
/// the two values in the mail looked IDENTICAL. The operator was asked, hourly, to apply a change
/// he could not see (measured PROD 2026-09-20).</para>
///
/// <para>🔑 Collapsing whitespace is the right answer rather than "show the difference better":
/// every surface that displays a title — the hub, the public agenda, Backstage — collapses runs of
/// whitespace anyway, so re-typing the title in Zoho would change NOTHING a visitor sees. Work that
/// cannot change an outcome should not be asked for.</para>
///
/// <para>⚠️ Only WHITESPACE is normalised. Case, punctuation, dashes and quotes are left exactly as
/// they are: those are visible, and a change to one is a change worth applying.</para>
/// </summary>
public static class TextComparison
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The value as a reader sees it: runs of whitespace collapsed to one space, trimmed.</summary>
    public static string CollapseWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : WhitespaceRun.Replace(value, " ").Trim();

    /// <summary>
    /// True when the two values differ in a way a reader would notice. Two values that differ only
    /// by whitespace are treated as EQUAL.
    /// </summary>
    public static bool DiffersVisibly(string? a, string? b) =>
        !string.Equals(CollapseWhitespace(a), CollapseWhitespace(b), StringComparison.Ordinal);
}
