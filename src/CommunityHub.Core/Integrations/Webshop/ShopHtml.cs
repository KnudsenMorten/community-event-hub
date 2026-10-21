using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// §1305 — renders the webshop's product HTML safely: an allow-list of text tags, EVERY attribute dropped.
/// </summary>
/// <remarks>
/// WordPress product descriptions are HTML written in a CMS — untrusted markup as far as CEH is concerned.
/// Stripping all attributes removes every vector that rides on one (<c>onclick</c>, <c>style</c>, <c>href</c>
/// with <c>javascript:</c>), and removing script/style blocks with their CONTENT stops code from surfacing as
/// text. What remains is paragraphs, lists and emphasis — all a product description needs.
/// </remarks>
public static class ShopHtml
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "ul", "ol", "li", "strong", "b", "em", "i", "h3", "h4", "h5",
    };

    private static readonly Regex Blocks = new(@"<(script|style|iframe|object|embed|noscript)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Tag = new(@"<\s*(/?)\s*([a-zA-Z0-9]+)[^>]*>", RegexOptions.Compiled);
    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Sanitised HTML, or empty for null/blank input.</summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var s = Comment.Replace(html, string.Empty);
        s = Blocks.Replace(s, string.Empty);
        s = Tag.Replace(s, m =>
        {
            var name = m.Groups[2].Value.ToLowerInvariant();
            if (!Allowed.Contains(name)) return name is "div" or "tr" ? " " : string.Empty;
            return name == "br" ? "<br>" : $"<{m.Groups[1].Value}{name}>";
        });
        // Any stray angle bracket left (a malformed tag) is escaped rather than trusted.
        s = Regex.Replace(s, "<(?!/?(p|br|ul|ol|li|strong|b|em|i|h3|h4|h5)>)", "&lt;", RegexOptions.IgnoreCase);
        return s.Trim();
    }

    /// <summary>Plain text (for cards and the contract).</summary>
    public static string PlainText(string? html, int max = 240)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var t = Regex.Replace(Blocks.Replace(html, " "), "<[^>]+>", " ");
        t = System.Net.WebUtility.HtmlDecode(t);
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > max ? t[..max].TrimEnd() + "…" : t;
    }
}
