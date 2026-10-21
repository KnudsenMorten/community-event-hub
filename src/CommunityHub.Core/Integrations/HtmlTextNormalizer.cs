using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1256c — turn a fragment of HTML into the PLAIN TEXT the hub actually stores and displays.
///
/// <para><b>Why.</b> Sessionize hands some biographies back wrapped in markup —
/// <c>&lt;p&gt;…&lt;/p&gt;</c>, the occasional <c>&lt;br&gt;</c> — and CEH stored it verbatim.
/// Nothing in the hub renders a bio as HTML (<c>Speakers/Detail</c> emits
/// <c>&lt;p class="spd-bio"&gt;@sp.Bio&lt;/p&gt;</c>, i.e. ENCODED), so the speaker's own page showed
/// the literal tags; and the "apply this in Backstage by hand" mail asked the operator to paste
/// those tags into a rich-text field, which would publish them on the public event site. Measured
/// in PROD 2026-09-20: 13 of 78 biographies carried tags.</para>
///
/// <para>🔒 <b>Structure becomes BLANK LINES, not nothing.</b> A bio is paragraphs; stripping tags
/// naively would run them together into one block and quietly destroy the author's formatting.
/// <c>&lt;/p&gt;</c> and <c>&lt;br&gt;</c> therefore become real line breaks before the remaining
/// tags are removed.</para>
///
/// <para>🔑 Deliberately NOT a general-purpose HTML parser. It handles the small, known shape that
/// arrives from Sessionize and from a paste out of Word — anything more would be a sanitizer, and a
/// half-written sanitizer is worse than none.</para>
/// </summary>
public static class HtmlTextNormalizer
{
    private static readonly Regex BreakTag =
        new(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BlockClose =
        new(@"<\s*/\s*(p|div|li|h[1-6])\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnyTag =
        new(@"<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex ThreePlusNewlines =
        new(@"(\r?\n){3,}", RegexOptions.Compiled);

    /// <summary>True when the value contains something this normalizer would change.</summary>
    public static bool LooksLikeHtml(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (AnyTag.IsMatch(value) || value.Contains('&') && Decoded(value) != value);

    /// <summary>
    /// Plain text: block ends and <c>&lt;br&gt;</c> become line breaks, every other tag is dropped,
    /// entities are decoded, runs of blank lines collapse to one, and the result is trimmed.
    /// Null/blank in ⇒ the same value out (never "" for null — a caller distinguishing "not set"
    /// from "set to empty" must keep that distinction).
    /// </summary>
    public static string? ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        var s = value.Replace("\r\n", "\n");
        s = BreakTag.Replace(s, "\n");
        s = BlockClose.Replace(s, "\n\n");
        s = AnyTag.Replace(s, string.Empty);
        s = Decoded(s);

        // Normalise the whitespace the tags left behind: trailing spaces on a line, and the
        // 3+ newlines that a "</p>\n<p>" pair produces.
        s = string.Join("\n", s.Split('\n').Select(line => line.TrimEnd()));
        s = ThreePlusNewlines.Replace(s, "\n\n");
        return s.Trim();
    }

    private static string Decoded(string s) => System.Net.WebUtility.HtmlDecode(s);
}
