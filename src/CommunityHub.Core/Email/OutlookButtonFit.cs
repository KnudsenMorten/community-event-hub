using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace CommunityHub.Core.Email;

/// <summary>
/// §1312 — every Outlook (VML) button is made wide enough for its own label, at the one point every mail passes through
/// before it is sent (<see cref="BrevoEmailSender.ApplyDefaultFont"/>).
/// </summary>
/// <remarks>
/// <para>Outlook for Windows draws a mail button as a <c>&lt;v:roundrect&gt;</c> of a FIXED width written in the template,
/// and cuts the label off when it does not fit ("Read and sign the", "Register and complete your"). Operator 2026-09-27:
/// <i>"i have said this 18 times now. check all buttons and make sure the width is big enough for the text. it fails all
/// times and i keep repeating this finding"</i>. It kept failing because each template's width was guessed by hand; the
/// fix is to compute it from the label, for every template, code-built button and saved override alike.</para>
/// <para>🔒 Only ever WIDENS. The estimate is deliberately generous (bold 16px Segoe/Aptos measured at 11.4–12.9 px per
/// character in Outlook; 0.85 em per character is used) and capped at <see cref="MaxWidth"/>, the mail's content width. A
/// label too long to fit even then is caught by a test over every template — shorten the label.</para>
/// </remarks>
public static class OutlookButtonFit
{
    /// <summary>The widest a button can be inside the 600px mail (32px padding each side), less a margin.</summary>
    public const int MaxWidth = 520;

    private const double EmPerChar = 0.85;
    private const int Padding = 56;

    private static readonly Regex Button = new(
        @"(<v:roundrect\b[^>]*?style=""[^""]*?\bwidth:)(\d+)(px[^""]*""[^>]*>)(.*?)(</v:roundrect>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The non-Outlook pill button: a link styled <c>border-radius:999px</c>, with its href, style and label.</summary>
    private static readonly Regex PillAnchor = new(
        @"<a\b(?<pre>[^>]*?)style=""(?<style>[^""]*?border-radius:999px[^""]*)""(?<post>[^>]*)>(?<label>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Href = new(@"\bhref=""(?<v>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Bg = new(@"background-color:\s*(?<v>#[0-9a-fA-F]{3,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Fg = new(@"(?<![-\w])color:\s*(?<v>#[0-9a-fA-F]{3,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Border = new(@"(?<![-\w])border:\s*(?<v>[^;""]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 🔴 §1312 — the pill becomes a BULLETPROOF button: the colour, border and padding live on a TABLE CELL, the link inside.
    /// Operator 2026-09-30, on the Get Started reminder: "the button in the email for sponsors looks weird. normally it is wider
    /// button" — the mail showed only the label on a tight blue background. Web / "new" Outlook drops a link's own width,
    /// line-height and padding, and after the fixed width was removed (so a long label would not wrap) nothing was left to make
    /// it a button. A table cell's bgcolor and padding survive every client; the label never wraps.
    /// </summary>
    private static string Bulletproof(Match m)
    {
        var style = m.Groups["style"].Value;
        var href = Href.Match(m.Groups["pre"].Value + " " + m.Groups["post"].Value) is { Success: true } h ? h.Groups["v"].Value : "#";
        var bg = Bg.Match(style) is { Success: true } b ? b.Groups["v"].Value : "#1565c0";
        var fg = Fg.Match(style) is { Success: true } f ? f.Groups["v"].Value : "#ffffff";
        var border = Border.Match(style) is { Success: true } br ? br.Groups["v"].Value.Trim() : $"2px solid {bg}";
        var size = FontSize.Match(style) is { Success: true } s ? s.Groups[1].Value : "16";
        return "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" align=\"center\" style=\"margin:0 auto;border-collapse:separate;\"><tr>"
            + $"<td align=\"center\" bgcolor=\"{bg}\" style=\"background-color:{bg};border:{border};border-radius:999px;padding:14px 32px;\">"
            + $"<a href=\"{href}\" target=\"_blank\" style=\"color:{fg};display:inline-block;font-family:Aptos,'Segoe UI',Arial,sans-serif;"
            + $"font-size:{size}px;font-weight:700;line-height:22px;text-align:center;text-decoration:none;white-space:nowrap;-webkit-text-size-adjust:none;\">"
            + m.Groups["label"].Value + "</a></td></tr></table>";
    }

    private static readonly Regex Tag = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex FontSize = new(@"font-size:\s*(\d+(?:\.\d+)?)px", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The width a label needs at a font size, uncapped.</summary>
    public static int Needed(string label, double fontPx = 16) =>
        (int)Math.Ceiling(label.Trim().Length * fontPx * EmPerChar) + Padding;

    /// <summary>The visible label and font size of each Outlook button in <paramref name="html"/> (for the template test).</summary>
    public static IReadOnlyList<(string Label, double FontPx, int Width)> Buttons(string html) =>
        Button.Matches(html ?? string.Empty).Select(m =>
            (LabelOf(m.Groups[4].Value), FontOf(m.Groups[4].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();

    /// <summary>Widen every Outlook button in <paramref name="html"/> to fit its label (never narrower than written).</summary>
    public static string Apply(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        // Every other client: the pill link becomes a bulletproof table-cell button, sized by its label (see Bulletproof).
        if (html.IndexOf("999px", StringComparison.Ordinal) >= 0)
            html = PillAnchor.Replace(html, Bulletproof);
        if (html.IndexOf("v:roundrect", StringComparison.OrdinalIgnoreCase) < 0) return html;
        return Button.Replace(html, m =>
        {
            var written = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var need = Math.Min(MaxWidth, Needed(LabelOf(m.Groups[4].Value), FontOf(m.Groups[4].Value)));
            return need <= written
                ? m.Value
                : m.Groups[1].Value + need.ToString(CultureInfo.InvariantCulture) + m.Groups[3].Value + m.Groups[4].Value + m.Groups[5].Value;
        });
    }

    private static string LabelOf(string inner) => WebUtility.HtmlDecode(Tag.Replace(inner, " ")).Trim();

    private static double FontOf(string inner) =>
        FontSize.Match(inner) is { Success: true } f ? double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture) : 16;
}
