using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1158 — the legal form belongs on the BILLING name and never on the public one.
///
/// <para>Operator 2026-08-31: <i>"the publicname in the webshop has wrong format for some sponsors …
/// it should not include legal terms like AG, Aps, A/S, LLC, K/S, Gmbh"</i> · <i>"billing name
/// includes fx A/S, Aps etc"</i> · <i>"public name is for linkedin"</i>.</para>
///
/// <para>⚠️ <b>The false positives are the risk, not the misses.</b> A missed suffix leaves one name
/// looking slightly formal; a wrong hit tells him to fix a name that is already right, and a couple
/// of those teach him to stop reading the mail. So the ambiguous cases below are asserted as
/// NON-matches deliberately.</para>
///
/// <para>NO real customer names — the names here are invented.</para>
/// </summary>
public sealed class CompanyLegalFormTests
{
    [Theory]
    [InlineData("Contoso A/S", "A/S", "Contoso")]
    [InlineData("Contoso ApS", "ApS", "Contoso")]
    [InlineData("Contoso Aps", "Aps", "Contoso")]          // his spelling
    [InlineData("Contoso K/S", "K/S", "Contoso")]
    [InlineData("Contoso AG", "AG", "Contoso")]
    [InlineData("Contoso Gmbh", "Gmbh", "Contoso")]        // his spelling
    [InlineData("Contoso GmbH", "GmbH", "Contoso")]
    [InlineData("Contoso Widgets, LLC", "LLC", "Contoso Widgets")]
    [InlineData("Contoso Ltd.", "Ltd.", "Contoso")]
    [InlineData("Contoso B.V.", "B.V.", "Contoso")]
    [InlineData("Contoso Oy", "Oy", "Contoso")]
    [InlineData("Contoso Widgets Inc", "Inc", "Contoso Widgets")]
    // 🔴 The four found in production on 2026-08-31, after the first sweep left them alone.
    [InlineData("Contoso Group AB", "AB", "Contoso Group")]
    [InlineData("CONTOSO SYSTEMS A/S", "A/S", "CONTOSO SYSTEMS")]
    [InlineData("Contoso Software Ltd.", "Ltd.", "Contoso Software")]
    [InlineData("Contoso sp. z o.o. sp. k.", "sp. z o.o. sp. k.", "Contoso")]
    public void A_trailing_legal_form_is_detected_and_stripped(
        string name, string expectedForm, string expectedStripped)
    {
        Assert.Equal(expectedForm, CompanyLegalForm.Detect(name));
        Assert.Equal(expectedStripped, CompanyLegalForm.Strip(name));
    }

    /// <summary>
    /// 🔑 The longest trailing run wins, or the suggestion keeps half the form.
    /// </summary>
    [Fact]
    public void A_multi_word_form_is_matched_whole()
    {
        Assert.Equal("GmbH & Co. KG", CompanyLegalForm.Detect("Contoso GmbH & Co. KG"));
        Assert.Equal("Contoso", CompanyLegalForm.Strip("Contoso GmbH & Co. KG"));
    }

    /// <summary>
    /// 🔴 A FIVE-token form — the one that got past the first production sweep.
    /// </summary>
    /// <remarks>
    /// A Polish limited partnership whose general partner is a limited company writes both forms in
    /// a row: "sp. z o.o. sp. k.". The trailing window was four tokens, so none of it matched and
    /// the name was left alone — indistinguishable, in the result, from a name that was already
    /// correct. That is why the window is six and why the sweep now lists what it left alone.
    /// </remarks>
    [Fact]
    public void A_stacked_five_token_form_is_matched_whole()
    {
        Assert.Equal("sp. z o.o. sp. k.", CompanyLegalForm.Detect("Contoso sp. z o.o. sp. k."));
        Assert.Equal("Contoso", CompanyLegalForm.Strip("Contoso sp. z o.o. sp. k."));

        // The single form on its own still works, and does not swallow the company name.
        Assert.Equal("Contoso", CompanyLegalForm.Strip("Contoso sp. z o.o."));
        Assert.Equal("Contoso Group", CompanyLegalForm.Strip("Contoso Group sp. k."));
    }

    [Theory]
    [InlineData("Nerdio")]
    [InlineData("Identity Stack")]
    [InlineData("CodeTwo")]
    [InlineData("Contoso Cloud Services")]
    public void A_clean_name_is_left_exactly_as_it_is(string name)
    {
        Assert.Null(CompanyLegalForm.Detect(name));
        Assert.Equal(name, CompanyLegalForm.Strip(name));
    }

    /// <summary>
    /// ⚠️ A form must be a whole trailing TOKEN — never a fragment inside a word.
    /// </summary>
    /// <remarks>
    /// "Incentive" contains "Inc", "Aspect" contains "ApS", "Vagabond" contains "AG". Substring
    /// matching would rename all three and destroy real company names.
    /// </remarks>
    [Theory]
    [InlineData("Incentive Systems")]
    [InlineData("Aspect Analytics")]
    [InlineData("Vagabond Travel")]
    [InlineData("Ltda Holdings")]
    public void A_form_hiding_inside_a_word_is_not_a_match(string name)
    {
        Assert.Null(CompanyLegalForm.Detect(name));
        Assert.Equal(name, CompanyLegalForm.Strip(name));
    }

    /// <summary>
    /// ⚠️ Only TRAILING. A leading or mid-name token is left alone.
    /// </summary>
    /// <remarks>
    /// "AG Consulting" is a company whose name begins with those letters, not a legal form — and
    /// stripping it would leave "Consulting", which names nobody.
    /// </remarks>
    [Theory]
    [InlineData("AG Consulting")]
    [InlineData("Inc Magazine Nordics")]
    public void A_form_that_is_not_at_the_end_is_left_alone(string name)
    {
        Assert.Null(CompanyLegalForm.Detect(name));
    }

    /// <summary>
    /// 🔒 A name that is ONLY a legal form is not stripped to nothing.
    /// </summary>
    /// <remarks>
    /// An empty public name silently falls back to the LEGAL name, so blanking one would publish
    /// the legal form — the exact state this feature removes. Better to leave it and report it.
    /// </remarks>
    [Fact]
    public void A_name_that_is_only_a_legal_form_is_not_emptied()
    {
        Assert.Null(CompanyLegalForm.Detect("A/S"));
        Assert.Equal("A/S", CompanyLegalForm.Strip("A/S"));
    }

    [Fact]
    public void Blank_in_blank_out_and_never_a_throw()
    {
        Assert.Null(CompanyLegalForm.Detect(null));
        Assert.Null(CompanyLegalForm.Detect("   "));
        Assert.Equal(string.Empty, CompanyLegalForm.Strip(null));
    }

    /// <summary>
    /// The joining punctuation goes with the form, not with the kept name.
    /// </summary>
    [Theory]
    [InlineData("Contoso, LLC", "Contoso")]
    [InlineData("Contoso  A/S", "Contoso")]
    [InlineData("Contoso - ApS", "Contoso")]
    public void The_separator_before_the_form_is_trimmed(string name, string expected)
    {
        Assert.Equal(expected, CompanyLegalForm.Strip(name));
    }

    /// <summary>
    /// 🔑 A marketing public name keeps its wording — only the form comes off the end.
    /// </summary>
    /// <remarks>
    /// The standing example in this codebase is a public name of the form
    /// "Brand - empowered by Partner". Reformatting that would be CEH overwriting someone's
    /// deliberate wording, which is the thing he was worried about automating.
    /// </remarks>
    [Fact]
    public void A_marketing_name_keeps_its_wording()
    {
        Assert.Equal(
            "Contoso - empowered by Fabrikam",
            CompanyLegalForm.Strip("Contoso - empowered by Fabrikam"));

        Assert.Equal(
            "Contoso - empowered by Fabrikam",
            CompanyLegalForm.Strip("Contoso - empowered by Fabrikam ApS"));
    }

    [Fact]
    public void A_caller_can_supply_its_own_list()
    {
        var forms = new[] { "ZZZ" };
        Assert.Equal("ZZZ", CompanyLegalForm.Detect("Contoso ZZZ", forms));
        // 🔒 An explicit list REPLACES the defaults — a caller narrowing the list must not silently
        // keep matching everything the default list knows.
        Assert.Null(CompanyLegalForm.Detect("Contoso A/S", forms));
    }
}
