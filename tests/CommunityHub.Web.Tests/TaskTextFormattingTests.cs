using CommunityHub.Branding;
using Microsoft.AspNetCore.Html;
using System.IO;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §600.5 — inline formatting in task copy: <b>bold</b>, <i>italic</i>, <u>underline</u>, bullets
/// and links. Operator 2026-07-28: <i>"it would also be create to include better formatting like
/// bold/italic/underline where appropiot"</i>.
///
/// <para><b>Why this matters beyond looks.</b> The task body was effectively plain text, which is
/// exactly why the existing copy fakes structure with ALL-CAPS pseudo-headings
/// (<c>DESIGN REQUIREMENTS:</c>) and <c>– or –</c> separators — there was no emphasis to reach for.
/// §600.4 calls the result unprofessional, and it is the first thing a paying sponsor sees.</para>
///
/// <para><b>The safety property these tests pin.</b> Task copy is authored in config/DB and rendered
/// into a sponsor-facing page, so the renderer HTML-ENCODES FIRST and only then substitutes its own
/// small set of tags. No raw HTML can ever pass through. A renderer that allowed it would be a
/// stored-XSS hole in a page shown to external companies.</para>
/// </summary>
public class TaskTextFormattingTests
{
    private static string Render(string? s)
    {
        var html = TaskTextLinkifier.Render(s);
        using var w = new StringWriter();
        html.WriteTo(w, System.Text.Encodings.Web.HtmlEncoder.Default);
        return w.ToString();
    }

    [Fact]
    public void Italic_renders_as_em()
    {
        Assert.Contains("<em>in good time</em>", Render("Please send the design *in good time*."));
    }

    [Fact]
    public void Bold_and_underline_still_render()
    {
        var html = Render("__DESIGN REQUIREMENTS__\n**File format:** vector");
        Assert.Contains("<u>DESIGN REQUIREMENTS</u>", html);
        Assert.Contains("<strong>File format:</strong>", html);
    }

    [Fact]
    public void Bold_wins_over_italic_so_double_asterisks_are_never_split()
    {
        // The ordering guarantee: **x** must not become <em>*x</em>*. Bold runs first and consumes
        // the pair, so nothing is left for the italic pass.
        var html = Render("**Coupon value: 99 EUR**");
        Assert.Contains("<strong>Coupon value: 99 EUR</strong>", html);
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void A_bullet_line_is_not_mistaken_for_italic()
    {
        // "* item" at line start is a BULLET. If the italic pass ran first it would pair that
        // asterisk with the next one on a later line and mangle the whole list.
        var html = Render("* Chair, black – EURO 35\n* Bar stool, white – EURO 35");
        Assert.Contains("•", html);
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void Raw_html_in_the_copy_is_encoded_not_executed()
    {
        // THE SECURITY PROPERTY. Task copy is authored data rendered into a sponsor-facing page.
        var html = Render("<script>alert('x')</script> and <b>not bold</b>");
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>not bold</b>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Emphasis_does_not_break_links_or_emails()
    {
        // Emphasis passes run before the link passes; the sentinel mechanism must survive them.
        var html = Render("Ask *Sabine* at sb@expertslive.dk or see [the spec](/Docs/Spec).");
        Assert.Contains("<em>Sabine</em>", html);
        Assert.Contains("mailto:sb@expertslive.dk", html);
        Assert.Contains("href=\"/Docs/Spec\"", html);
    }

    [Fact]
    public void Unpaired_asterisks_are_left_alone()
    {
        // A lone asterisk (a footnote marker, a times sign) must not eat the rest of the line.
        var html = Render("Size 3m * 2.3m and a note* here");
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void Highlight_marks_the_one_line_that_must_not_be_missed()
    {
        // §675 (operator 2026-07-29, on the DSV shipment pricing): "make it part with price high
        // yellow as important". Bold is already spent on product names and addresses in these
        // bodies, so it no longer stands out — a highlight is a different level of emphasis.
        var html = Render("==the pricing is fairly high== but the rest is normal");
        Assert.Contains("<mark", html);
        Assert.Contains("the pricing is fairly high", html);
        Assert.DoesNotContain("==", html);
    }

    [Fact]
    public void Highlight_composes_with_bold_without_either_eating_the_other()
    {
        var html = Render("==Pricing is high.== This is **out of our hands** as organizers.");
        Assert.Contains("<mark", html);
        Assert.Contains("<strong>out of our hands</strong>", html);
    }

    [Fact]
    public void An_unpaired_or_bare_equals_sign_is_left_alone()
    {
        // Equals signs occur naturally in task copy (dimensions, formulas, query strings) and must
        // not start a highlight that swallows the rest of the line.
        var html = Render("Booth = 3m wide and total = 12 sqm");
        Assert.DoesNotContain("<mark", html);
    }
}
