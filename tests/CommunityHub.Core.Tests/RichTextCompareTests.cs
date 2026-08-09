using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §989 — the shared rich-text comparison that decides whether a long Zoho field still matches
/// CEH. Every case here is a difference the Zoho EDITOR introduces (and must therefore be
/// folded) or a difference a PERSON made (and must therefore survive). Getting that boundary
/// wrong in either direction is a §594 failure: fold too much and a real edit goes unreported,
/// fold too little and the operator gets an ACTION line he cannot close by acting.
/// </summary>
public class RichTextCompareTests
{
    // ---- IsEffectivelyBlank: what the operator SEES as an empty box -------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<p></p>")]
    [InlineData("<p><br></p>")]
    [InlineData("<p><br/></p>")]
    // 🔴 THE REGRESSION §989 FIXES. The old tag-only StripHtml left the literal "&nbsp;" here,
    // read it as a filled description, and so NEVER reported the empty-description gap —
    // the §322l complaint ("session description is still missing in zoho").
    [InlineData("<p>&nbsp;</p>")]
    [InlineData("<div>&nbsp;&nbsp;</div>")]
    [InlineData(" ")]
    [InlineData("<p>​</p>")]
    public void Editor_empty_values_are_blank(string? value) =>
        Assert.True(RichTextCompare.IsEffectivelyBlank(value));

    [Theory]
    [InlineData("<p>Real text</p>")]
    [InlineData("x")]
    [InlineData("<p>&amp;</p>")]
    public void Real_content_is_not_blank(string value) =>
        Assert.False(RichTextCompare.IsEffectivelyBlank(value));

    // ---- Comparable: the normalization itself ------------------------------------------------

    [Fact]
    public void Tags_become_a_space_so_paragraphs_do_not_run_together()
    {
        // "<p>one</p><p>two</p>" must not compare equal to "onetwo".
        Assert.Equal("one two", RichTextCompare.Comparable("<p>one</p><p>two</p>"));
        Assert.NotEqual(
            RichTextCompare.Comparable("onetwo"),
            RichTextCompare.Comparable("<p>one</p><p>two</p>"));
    }

    [Fact]
    public void Entities_whitespace_and_typography_are_folded()
    {
        Assert.Equal("a & b", RichTextCompare.Comparable("a &amp; b"));
        Assert.Equal("one two", RichTextCompare.Comparable("one \r\n\t  two"));
        Assert.Equal("it's", RichTextCompare.Comparable("it’s"));           // curly apostrophe
        Assert.Equal("\"q\"", RichTextCompare.Comparable("“q”"));      // curly quotes
        Assert.Equal("a-b", RichTextCompare.Comparable("a—b"));             // em dash
        Assert.Equal("wait...", RichTextCompare.Comparable("wait…"));       // ellipsis
    }

    // ---- DiffersFromCeh: the decision ---------------------------------------------------------

    [Fact]
    public void A_reformatted_but_identical_description_is_NOT_drift()
    {
        // The §594 case in one assertion: Zoho re-wrapped the text in <p>, converted the spacing
        // to &nbsp; and re-curled the apostrophe on save. Nobody edited anything. If this ever
        // reports true, every session is mailed forever and the operator cannot close a single
        // line by pasting — which is precisely why the tags diff was removed.
        const string ceh = "Zero Trust isn't a product. It's an architecture - and it starts here.";
        const string zohoAfterSave =
            "<p>Zero Trust isn’t a product.&nbsp;It’s an architecture – and it starts here.</p>";

        Assert.False(RichTextCompare.DiffersFromCeh(zohoAfterSave, ceh, ignoreCase: false));
    }

    [Fact]
    public void A_genuinely_changed_description_IS_drift()
    {
        // §983: the abstract is import-owned, so a Sessionize edit lands in CEH silently. Before
        // §989 a non-blank live description was never looked at and Zoho stayed stale for ever.
        Assert.True(RichTextCompare.DiffersFromCeh(
            "<p>The old abstract.</p>", "The NEW abstract, edited in Sessionize.", ignoreCase: false));
    }

    [Fact]
    public void An_editor_blank_zoho_value_is_drift_when_CEH_has_text() =>
        Assert.True(RichTextCompare.DiffersFromCeh("<p>&nbsp;</p>", "Real abstract", ignoreCase: false));

    [Fact]
    public void A_blank_CEH_value_is_never_drift() =>
        // Nothing to paste — reporting it would be an ACTION line with no action.
        Assert.False(RichTextCompare.DiffersFromCeh("<p>Zoho has text</p>", "   ", ignoreCase: false));

    [Fact]
    public void Case_follows_the_call_site_policy()
    {
        // Prose (sessions): a case change is a real edit and must surface.
        Assert.True(RichTextCompare.DiffersFromCeh("the abstract", "The Abstract", ignoreCase: false));
        // URLs / company text (sponsors, §792): case is noise.
        Assert.False(RichTextCompare.DiffersFromCeh("the abstract", "The Abstract", ignoreCase: true));
    }
}
