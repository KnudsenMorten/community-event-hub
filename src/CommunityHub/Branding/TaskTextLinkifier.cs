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
    // pass so the captured URL is not double-linkified. Accepts absolute
    // http(s) URLs AND app-relative "/..." routes — the wizard/master-class
    // task seeders write [Open the Hotel form](/Forms/Hotel)-style deep
    // links, which must render as buttons, not raw markdown text.
    private static readonly Regex MarkdownLinkPattern = new(
        @"\[([^\]\n]+?)\]\(((?:https?://|/)[^\s)]+)\)",
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

    // §600.5 — *italic*, completing the set the operator asked for: *"it would also be create to
    // include better formatting like bold/italic/underline where appropiot"*.
    //
    // ORDER MATTERS and is why this is safe: it runs AFTER the bullet pass (a line-start "* " has
    // already become "• ") and AFTER the bold pass (which consumed every "**x**"), so any asterisk
    // pair reaching here is genuinely single-asterisk emphasis. Inner text carries no asterisk or
    // newline, so it can neither span lines nor swallow a following bullet.
    private static readonly Regex ItalicPattern = new(
        @"\*(?=\S)([^*\n]+?)\*",
        RegexOptions.Compiled);

    // §675 — ==highlight== -> a yellow <mark>, for the one line in a task that must not be missed
    // (operator 2026-07-29, on the DSV shipment pricing: "make it part with price high yellow as
    // important"). Bold was already spent on product names and addresses throughout these bodies,
    // so it no longer stands out; a highlight is a genuinely different level of emphasis.
    //
    // Uses '=' rather than another asterisk/underscore form precisely BECAUSE it cannot collide
    // with the bold/italic/underline passes above, so pass order does not matter here.
    private static readonly Regex HighlightPattern = new(
        @"==(?=\S)([^=\n]+?)==",
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

        // Pass 0d: *italic* -> <em>. Must follow the bold pass — see ItalicPattern.
        encoded = ItalicPattern.Replace(encoded, "<em>$1</em>");

        // Pass 0e: ==highlight== -> a yellow mark. Inline styled rather than class-based so it
        // survives everywhere a task body is rendered (including the plainer e-mail bodies).
        encoded = HighlightPattern.Replace(
            encoded,
            "<mark style=\"background:#fff3cd;color:#664d03;padding:0 3px;border-radius:3px;\">"
            + "$1</mark>");

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
            // App-relative routes navigate in the SAME tab (in-hub deep link);
            // only external http(s) links open a new tab.
            anchors.Add(url.StartsWith('/')
                ? $"<a class=\"task-link-btn\" href=\"{url}\">{label}</a>"
                : ExternalButton(url, label));
            return $"\u0001MD{anchors.Count - 1}\u0002";
        });

        // Pass 2: bare URLs become buttons labelled with the URL.
        encoded = UrlPattern.Replace(encoded, m =>
        {
            var url = m.Value; // already HTML-encoded above
            return ExternalButton(url, url);
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

    /// <summary>
    /// §653 — an EXTERNAL task button: opens a new tab, and SAYS SO with the same ↗ indicator the
    /// navigation uses.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-29: *"i also need to have the external indicator (similar to zoho in
    /// menu-item) for any button that opens up external urls in tasks"*. The nav has carried
    /// <c>&#8599;</c> plus a screen-reader "opens in a new tab" since §176; a task button that
    /// leaves the hub is exactly the same promise to the reader, and it was the one place not
    /// keeping it.</para>
    ///
    /// <para>🔒 <b>The webshop link additionally gets the hand-off notice.</b> Same treatment as the
    /// Zoho menu item (§487): a sponsor arriving at a login screen with no warning assumes the hub
    /// logged them out. The dialog explains that this is a DIFFERENT system with its own password —
    /// which is the actual confusion, not the new tab.</para>
    /// </remarks>
    private static string ExternalButton(string url, string label)
    {
        // §672 — the marker read by the layout's interstitial handler, now chosen by KIND rather
        // than by a single hard-coded "is this the webshop?" flag. The layout has ALWAYS had both
        // handlers (data-zoho-interstitial and data-webshop-interstitial, with separate storage
        // keys so dismissing one does not silently dismiss the other) — task buttons could just
        // never emit the Zoho one, which is why the leads/inquiries buttons dropped the sponsor
        // straight onto a Zoho sign-in screen with no explanation.
        //
        // 🔒 Selection stays EXPLICIT, never "external ⇒ warn". §176/§487: a hand-off notice on
        // links that have no separate sign-in is noise, and noise is how people learn to click
        // through the one dialog that mattered.
        var marker = InterstitialFor(url) switch
        {
            ExternalInterstitialKind.Webshop => " data-webshop-interstitial=\"1\"",
            ExternalInterstitialKind.Zoho    => " data-zoho-interstitial=\"1\"",
            _ => string.Empty,
        };

        return $"<a class=\"task-link-btn\" href=\"{url}\" target=\"_blank\" "
             + $"rel=\"noopener noreferrer\"{marker}>{label}"
             + "<span class=\"ext-ico\" aria-hidden=\"true\">&#8599;</span>"
             + "<span class=\"visually-hidden\"> (opens in a new tab)</span></a>";
    }

    /// <summary>
    /// §672 — which sign-in hand-off notice (if any) an external task button needs. The real
    /// confusion these explain is not the new tab: it is landing in a DIFFERENT system with its
    /// own sign-in and assuming the hub logged you out (§487).
    /// </summary>
    private enum ExternalInterstitialKind
    {
        None,
        Webshop,
        Zoho,
    }

    private static ExternalInterstitialKind InterstitialFor(string url)
    {
        if (IsSponsorWebshop(url))
        {
            return ExternalInterstitialKind.Webshop;
        }

        return IsZohoBackstage(url) ? ExternalInterstitialKind.Zoho : ExternalInterstitialKind.None;
    }

    /// <summary>True for the external sponsor webshop, which needs the sign-in hand-off notice.</summary>
    private static bool IsSponsorWebshop(string url) =>
        url.Contains("expertslive.dk/sponsor", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/sponsor-shop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a Zoho Backstage EXHIBITOR-DASHBOARD deep link — the pages that demand a separate
    /// Backstage sign-in (e-mail + one-time PIN).
    /// </summary>
    /// <remarks>
    /// Matched on the <c>exhibitor-dashboard</c> route rather than on the edition's host, so it
    /// stays evergreen (a different community's Backstage portal has a different host but the same
    /// route) and stays NARROW: a public Backstage page such as the venue/floor-plan link needs no
    /// sign-in and must not raise the dialog.
    /// </remarks>
    private static bool IsZohoBackstage(string url) =>
        url.Contains("exhibitor-dashboard", StringComparison.OrdinalIgnoreCase);
}
