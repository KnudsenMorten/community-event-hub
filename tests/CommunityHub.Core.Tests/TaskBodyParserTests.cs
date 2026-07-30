using CommunityHub.Core.Tasks.Model;
using CommunityHub.Core.Tasks.Parsing;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §684.8 — the directive parser. The set is CLOSED, and everything it refuses it refuses LOUDLY
/// (a diagnostic) rather than by putting raw markup on a sponsor's page.
/// </summary>
public class TaskBodyParserTests
{
    private static TaskBody Parse(string source) => TaskBodyParser.Parse(source);

    [Fact]
    public void Prose_becomes_paragraphs_split_on_blank_lines()
    {
        var body = Parse("First paragraph.\n\nSecond paragraph.");

        Assert.True(body.IsValid);
        Assert.Equal(2, body.Blocks.Count);
        Assert.All(body.Blocks, b => Assert.IsType<TaskParagraph>(b));
    }

    [Fact]
    public void Section_directive_replaces_the_underline_heading_convention()
    {
        // §669 — the operator's complaint was that "STEP 1 — ORDER" was only UNDERLINED while
        // lesser details below it were bold, so the scannable structure was the quietest thing on
        // the page. A heading is a TYPE now, so that inversion cannot be re-authored.
        var body = Parse(":::section STEP 1 — ORDER\nDo the thing.\n:::");

        var section = Assert.IsType<TaskSection>(Assert.Single(body.Blocks));
        Assert.Equal("STEP 1 — ORDER", section.Title);
        Assert.IsType<TaskParagraph>(Assert.Single(section.Children));
    }

    [Fact]
    public void Button_is_a_block_with_declared_style_target_and_interstitial()
    {
        var body = Parse(
            ":::button\nlabel: Open the Sponsor Webshop\nhref: https://shop.example/x\n"
            + "style: primary\ninterstitial: webshop\n:::");

        var button = Assert.IsType<TaskButton>(Assert.Single(body.Blocks));
        Assert.Equal("Open the Sponsor Webshop", button.Label);
        Assert.Equal(TaskButtonStyle.Primary, button.Style);
        Assert.Equal(TaskInterstitial.Webshop, button.Interstitial);
        Assert.Equal(TaskLinkTarget.External, button.Target);
    }

    [Fact]
    public void App_relative_button_defaults_to_in_hub_with_no_external_indicator()
    {
        // 🔒 §671 — "Open Booth Members" carried the ↗ external indicator and opened a new tab
        // while pointing at an in-hub page, so the button contradicted the sentence above it.
        // Deriving the target FROM the href is what makes that unrepeatable.
        var body = Parse(
            ":::button\nlabel: Open Booth Members\nhref: /Sponsor/CompanyDetails#booth-members\n:::");

        var button = Assert.IsType<TaskButton>(Assert.Single(body.Blocks));
        Assert.Equal(TaskLinkTarget.InHub, button.Target);
        Assert.Equal(TaskInterstitial.None, button.Interstitial);
    }

    [Fact]
    public void An_in_hub_button_may_not_carry_an_interstitial()
    {
        // §176/§487 — a sign-in hand-off notice where there is no second system to sign in to is
        // noise, and noise is how people learn to click through the one dialog that mattered.
        var body = Parse(
            ":::button\nlabel: Company Details\nhref: /Sponsor/CompanyDetails\ninterstitial: zoho\n:::");

        Assert.Empty(body.Blocks);
        Assert.Contains(body.Diagnostics, d => d.Message.Contains("in-hub button"));
    }

    [Fact]
    public void A_button_href_outside_the_allow_list_is_refused()
    {
        // §684.17 — URL handling is a typed field with an allow-list, not a regex over prose.
        var body = Parse(":::button\nlabel: Click\nhref: javascript:alert(1)\n:::");

        Assert.Empty(body.Blocks);
        Assert.Contains(body.Diagnostics, d => d.Message.Contains("not allowed"));
    }

    [Fact]
    public void An_unknown_directive_renders_as_nothing_and_reports_why()
    {
        // 🔒 The closed set's whole promise: never raw text on a sponsor's page, and never silent.
        var body = Parse(":::marquee\nlabel: nope\n:::");

        Assert.Empty(body.Blocks);
        var diagnostic = Assert.Single(body.Diagnostics);
        Assert.Contains(":::marquee", diagnostic.Message);
    }

    [Fact]
    public void An_unclosed_directive_is_reported_rather_than_swallowing_the_document()
    {
        var body = Parse(":::section STEP 1\nSomething.");

        Assert.Contains(body.Diagnostics, d => d.Message.Contains("never closed"));
    }

    [Fact]
    public void A_decision_needs_both_labels_because_declining_is_a_recorded_answer()
    {
        // 🔒 §670 — organizers need to know who DECLINED versus who never answered; today both look
        // like "not complete". A decline label is therefore not optional.
        var body = Parse(":::decision appGame\naccept: We would like to participate\n:::");

        Assert.Empty(body.Blocks);
        Assert.Contains(body.Diagnostics, d => d.Message.Contains("recorded answer"));
    }

    [Fact]
    public void Decision_parses_with_an_optional_follow_up_form()
    {
        // §676 requires the SAME component as §670, with the delivery question as an OPTION of it
        // rather than a copy — which is what the nullable form ref expresses.
        var body = Parse(
            ":::decision appGame\naccept: We would like to participate\ndecline: No interest\n"
            + "form: app-game\n:::");

        var decision = Assert.IsType<TaskDecision>(Assert.Single(body.Blocks));
        Assert.Equal("appGame", decision.Key);
        Assert.Equal("app-game", decision.AcceptForm);
    }

    [Fact]
    public void Data_directive_names_a_provider()
    {
        var body = Parse(":::data tvPurchase\n:::");

        Assert.Equal("tvPurchase", Assert.IsType<TaskData>(Assert.Single(body.Blocks)).Source);
    }

    [Fact]
    public void A_button_cannot_be_authored_inside_a_bullet()
    {
        // 🔒 §677 — the sponsor-wall task rendered an empty "•" followed by a block button, because
        // a button could be INFERRED from a bare URL inside a bullet. List items hold inline content
        // only and a button is a block, so that layout is now unrepresentable rather than merely
        // discouraged. The directive here is not a list item at all; it is its own block.
        var body = Parse("* Format: vector\n\n:::button\nlabel: Spec\nhref: https://x.example/s\n:::");

        Assert.Collection(
            body.Blocks,
            b => Assert.IsType<TaskList>(b),
            b => Assert.IsType<TaskButton>(b));
        var list = (TaskList)body.Blocks[0];
        Assert.Single(list.Items);
    }

    [Fact]
    public void Ordered_and_bulleted_lists_are_distinguished()
    {
        var ordered = Parse("1. First\n2. Second");
        var bulleted = Parse("* First\n* Second");

        Assert.True(Assert.IsType<TaskList>(Assert.Single(ordered.Blocks)).Ordered);
        Assert.False(Assert.IsType<TaskList>(Assert.Single(bulleted.Blocks)).Ordered);
    }
}
