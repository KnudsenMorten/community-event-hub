using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §685 — the PLAIN-TEXT task renderer had silently fallen two features behind the web one.
///
/// <para>
/// `TaskTextLinkifier` (web) grew <c>*italic*</c> in §600.5 and <c>==highlight==</c> in §675.
/// `TaskMarkup.ToPlainText` — used by SIX call sites to build the ICS <c>DESCRIPTION</c> for
/// "Send Reminder to My Calendar" — knew neither, so the raw markers went into the calendar
/// entry. Nobody saw it because nobody reads their own .ics file.
/// </para>
///
/// <para>
/// 🔒 This is the concrete evidence behind §684.10: two hand-rolled renderers of the same markup
/// WILL drift, and nothing makes them drift loudly. A closed block model turns "add a marker"
/// into a compiler error listing every renderer that must handle it.
/// </para>
/// </summary>
public class TaskMarkupPlainTextTests
{
    [Fact]
    public void Highlight_markers_do_not_leak_into_plain_text()
    {
        // §675's DSV shipment copy, verbatim in shape.
        var text = TaskMarkup.ToPlainText(
            "==Please be aware that the pricing for these services is fairly high.== This is set by Bella Center.");

        Assert.DoesNotContain("==", text, StringComparison.Ordinal);
        Assert.Contains("Please be aware that the pricing for these services is fairly high.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Italic_markers_do_not_leak_into_plain_text()
    {
        var text = TaskMarkup.ToPlainText("We print it well before the event, so *timing matters*.");

        Assert.Equal("We print it well before the event, so timing matters.", text);
    }

    /// <summary>
    /// 🔒 The ordering trap: bold is <c>**x**</c> and italic is <c>*x*</c>. If italic ran first it
    /// would eat one asterisk from each side of a bold span and leave stray markers behind.
    /// </summary>
    [Fact]
    public void Bold_and_italic_together_leave_no_stray_asterisks()
    {
        var text = TaskMarkup.ToPlainText("The cost is **EURO 500 (excl. tax)** and *timing matters*.");

        Assert.Equal("The cost is EURO 500 (excl. tax) and timing matters.", text);
        Assert.DoesNotContain("*", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔒 A "* " list marker at the start of a line must become a bullet, NOT be read as the
    /// opening of an italic span — which is why the bullet pass runs last.
    /// </summary>
    [Fact]
    public void A_bullet_line_is_not_mistaken_for_italic()
    {
        var text = TaskMarkup.ToPlainText("* First item\n* Second item");

        Assert.Contains("• First item", text, StringComparison.Ordinal);
        Assert.Contains("• Second item", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_existing_bold_underline_and_link_handling_is_unchanged()
    {
        var text = TaskMarkup.ToPlainText(
            "__Shipment to__ **Pelican Amagerbro** [Open the Sponsor Webshop](https://expertslive.dk/sponsor)");

        Assert.Contains("Shipment to", text, StringComparison.Ordinal);
        Assert.Contains("Pelican Amagerbro", text, StringComparison.Ordinal);
        Assert.Contains("Open the Sponsor Webshop: https://expertslive.dk/sponsor", text, StringComparison.Ordinal);
        Assert.DoesNotContain("__", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bare "=" (dimensions, formulas) must survive — the same property §675 pinned on the web
    /// side. A highlight needs a matching pair.
    /// </summary>
    [Fact]
    public void A_bare_equals_sign_is_left_alone()
    {
        var text = TaskMarkup.ToPlainText("Booth size = 3x2 m");

        Assert.Equal("Booth size = 3x2 m", text);
    }

    /// <summary>
    /// The heading shape §669 introduced across the catalogue: underline wrapping bold. Both
    /// markers must come off, leaving the heading text.
    /// </summary>
    [Fact]
    public void The_669_heading_shape_reduces_to_plain_text()
    {
        var text = TaskMarkup.ToPlainText("__**STEP 1 — ORDER:**__");

        Assert.Equal("STEP 1 — ORDER:", text);
    }
}
