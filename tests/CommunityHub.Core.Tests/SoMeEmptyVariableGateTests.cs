using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §922 — A POST CANNOT GO OUT WITH AN EMPTY VARIABLE IN IT.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"a post can NOT go out if a variable is empty in the post. that is
/// the blocker. that covers fx missing sponsor some text or session text, agree?"</i></para>
///
/// <para>🔑 The two distinctions that make the rule safe rather than merely strict:
/// <b>empty ≠ unknown</b>, and <b>some variables are empty by design</b>. Both are pinned below,
/// because a rule this broad fails by blocking things nobody can ever unblock.</para>
/// </remarks>
public sealed class SoMeEmptyVariableGateTests
{
    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    /// <summary>His two examples, both blocked.</summary>
    [Theory]
    [InlineData("{SponsorSocialMediaCompanyDescription}", "SponsorSocialMediaCompanyDescription")]
    [InlineData("{IntroText}", "IntroText")]
    [InlineData("{SessionTeaserTextAI}", "SessionTeaserTextAI")]
    public void A_known_variable_with_no_value_blocks_the_post(string body, string token)
    {
        var values = Values((token, null));

        Assert.Contains(token, SoMeEmptyVariableGate.MissingRequired(body, values));
        Assert.NotNull(SoMeEmptyVariableGate.ReasonFor(body, values));
    }

    [Fact]
    public void A_variable_with_a_value_does_not_block()
    {
        var values = Values(("SponsorName", "Robopack"));

        Assert.Empty(SoMeEmptyVariableGate.MissingRequired("Hello {SponsorName}", values));
        Assert.Null(SoMeEmptyVariableGate.ReasonFor("Hello {SponsorName}", values));
    }

    /// <summary>
    /// ⚠️ EMPTY ≠ UNKNOWN. A typo'd token is a TEMPLATE BUG: §864.3 publishes it verbatim so it is
    /// visible, rather than withholding the post for ever and telling nobody. Blocking here would
    /// hide the typo behind a post that never goes out.
    /// </summary>
    [Fact]
    public void An_unknown_token_is_not_treated_as_a_missing_dependency()
    {
        // Nothing knows {SponsorNmae} — it is a misspelling, not an empty value.
        var values = Values(("SponsorName", "Robopack"));

        Assert.Empty(SoMeEmptyVariableGate.MissingRequired("Hello {SponsorNmae}", values));
    }

    /// <summary>
    /// 🔒 SOME VARIABLES ARE EMPTY BY DESIGN. §884.3 built "Tag: {SponsorSigner}
    /// {SponsorEventCoordinators}" so a sponsor with no mentionable contact simply has no "Tag:"
    /// line — measured 2026-08-06, 0 of 14 sponsors have a LinkedIn organisation id, so blocking on
    /// these would block those posts for ever waiting for something that is never coming.
    /// </summary>
    [Theory]
    [InlineData("SponsorSigner")]
    [InlineData("SponsorEventCoordinators")]
    [InlineData("Action_catalog_random")]
    [InlineData("SponsorHashtag")]
    // 🔴 §926 — "SessionAbstract" WAS on this list and is not any more. Operator 2026-08-06:
    // *"master class announcement and sessions has dependency to description. if empty it is not
    // ready"*. It is the source the AI teaser is written FROM, so an empty one does not shorten the
    // post — it invents one.
    public void A_deliberately_optional_variable_never_blocks(string token)
    {
        var values = Values((token, null));

        Assert.Empty(SoMeEmptyVariableGate.MissingRequired($"Tag: {{{token}}}", values));
    }

    /// <summary>
    /// 🔒 REQUIRED BY DEFAULT — the safe direction. A variable nobody has classified blocks until
    /// someone decides it is optional, rather than slipping out empty because it was forgotten.
    /// </summary>
    [Fact]
    public void An_unclassified_variable_is_required_by_default()
    {
        var values = Values(("SomeBrandNewThing", null));

        Assert.Contains("SomeBrandNewThing",
            SoMeEmptyVariableGate.MissingRequired("x {SomeBrandNewThing} y", values));
    }

    /// <summary>The reason names the variables, because that is what someone can act on (§850).</summary>
    [Fact]
    public void The_reason_says_what_is_missing_in_words()
    {
        var reason = SoMeEmptyVariableGate.ReasonFor(
            "{IntroText} and {SponsorSocialMediaCompanyDescription}",
            Values(("IntroText", null), ("SponsorSocialMediaCompanyDescription", null)));

        Assert.NotNull(reason);
        Assert.Contains("teaser", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("social-media text", reason, StringComparison.OrdinalIgnoreCase);
        // Not a bare token dump — a person has to read this.
        Assert.DoesNotContain("{IntroText}", reason, StringComparison.Ordinal);
    }

    /// <summary>Whitespace is empty. A value of " " is not a value.</summary>
    [Fact]
    public void A_whitespace_only_value_counts_as_empty()
    {
        Assert.Contains("SponsorName",
            SoMeEmptyVariableGate.MissingRequired("{SponsorName}", Values(("SponsorName", "   "))));
    }

    [Fact]
    public void An_empty_body_blocks_nothing()
    {
        Assert.Empty(SoMeEmptyVariableGate.MissingRequired(null, Values()));
        Assert.Null(SoMeEmptyVariableGate.ReasonFor("", Values()));
    }
}
