using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace CommunityHub.Branding;

/// <summary>
/// Renders a plain-text task description as HTML where every http(s) URL
/// and every email address becomes a styled "task-link-btn" anchor.
/// JSON-side authors keep writing plain prose -- the hub auto-detects
/// links so a description like
///   "Send the info to info@expertslive.dk"
/// renders as
///   "Send the info to [✉ info@expertslive.dk]"   (button)
///
/// HTML-encodes first, then replaces detected patterns -- so any literal
/// &lt;tag&gt; characters in the JSON stay encoded and the only HTML
/// elements rendered are the anchors we control. Whitespace is preserved
/// with white-space:pre-line on the rendering &lt;p&gt;.
/// </summary>
public static class TaskTextLinkifier
{
    // Markdown-style [label](url) so JSON authors can show a friendly
    // button label without exposing a long URL. Runs BEFORE the bare-URL
    // pass so the captured URL is not double-linkified.
    private static readonly Regex MarkdownLinkPattern = new(
        @"\[([^\]\n]+?)\]\((https?://[^\s)]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Anchored to whitespace / start / end so we don't eat trailing
    // punctuation. Excludes common terminators (.,;:!?) from the URL tail
    // -- "the URL is https://foo.com/path." should NOT include the period.
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s<>""'`]+?(?=[\s<>""'`]|[.,;:!?)\]}](?:\s|$)|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Stricter than the URL pattern -- requires the email shape with
    // a real domain (TLD 2+ chars). Trailing punctuation is excluded by
    // the regex itself (no period at the end of the match).
    private static readonly Regex EmailPattern = new(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
        RegexOptions.Compiled);

    // JSON authors write list items with a leading "* " (or "- "); render them
    // as TRUE bullets (a "• " glyph) since the text is shown verbatim under
    // white-space:pre-line (no markdown list parsing).
    private static readonly Regex BulletPattern = new(
        @"(?m)^[ \t]*[*-][ \t]+",
        RegexOptions.Compiled);

    // Markdown-style **bold** so JSON authors can emphasise short bits (coupon
    // code/value, etc.) without raw HTML (which is encoded away). Inner text has
    // no '*' or newline; runs AFTER the bullet pass (a line-start "* " is already
    // a bullet, and "**x**" never starts with "* ") and is unaffected by it.
    private static readonly Regex BoldPattern = new(
        @"\*\*(?=\S)([^*\n]+?)\*\*",
        RegexOptions.Compiled);

    // Markdown-ish __underline__ -> <u> for section headers. Distinct from
    // **bold**; inner text has no underscore/newline.
    private static readonly Regex UnderlinePattern = new(
        @"__(?=\S)([^_\n]+?)__",
        RegexOptions.Compiled);

    public static IHtmlContent Render(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return HtmlString.Empty;
        }

        var encoded = WebUtility.HtmlEncode(description);

        // Pass 0: turn "* item" / "- item" line starts into true bullets.
        encoded = BulletPattern.Replace(encoded, "• ");

        // Pass 0b: **bold** -> <strong>.
        encoded = BoldPattern.Replace(encoded, "<strong>$1</strong>");

        // Pass 0c: __underline__ -> <u>.
        encoded = UnderlinePattern.Replace(encoded, "<u>$1</u>");

        // Pass 1: extract markdown links to a side-buffer and leave a
        // sentinel in the text. We MUST do this before the URL pass --
        // otherwise the URL inside the rendered <a href="..."> tag would
        // be matched again by the bare-URL pass and the markup explodes
        // (double-wrapped anchor with leaked attributes). The sentinel
        // contains no http/email shape so subsequent passes ignore it,
        // and we splice the real anchors back in at the very end.
        var anchors = new List<string>();
        encoded = MarkdownLinkPattern.Replace(encoded, m =>
        {
            var label = m.Groups[1].Value; // already HTML-encoded above
            var url   = m.Groups[2].Value;
            anchors.Add($"<a class=\"task-link-btn\" href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer\">{label}</a>");
            return $"\u0001MD{anchors.Count - 1}\u0002";
        });

        // Pass 2: bare URLs become buttons labelled with the URL.
        encoded = UrlPattern.Replace(encoded, m =>
        {
            var url = m.Value; // already HTML-encoded above
            return $"<a class=\"task-link-btn\" href=\"{url}\" target=\"_blank\" rel=\"noopener noreferrer\">{url}</a>";
        });

        // Emails render as a PLAIN inline link (not a boxed button) so the
        // address itself is the clickable text and reads inline with the prose.
        encoded = EmailPattern.Replace(encoded, m =>
        {
            var email = m.Value; // already HTML-encoded above
            return $"<a href=\"mailto:{email}\" style=\"color:#1565c0;text-decoration:underline;\">{email}</a>";
        });

        // Splice the stored markdown anchors back in.
        for (var i = 0; i < anchors.Count; i++)
        {
            encoded = encoded.Replace($"\u0001MD{i}\u0002", anchors[i]);
        }

        return new HtmlString(encoded);
    }
}
