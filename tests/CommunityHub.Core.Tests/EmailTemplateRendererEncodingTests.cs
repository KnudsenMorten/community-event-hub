using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the renderer's HTML-encoding contract (REQUIREMENTS §10c-4 —
/// "encode at the seam"). Plain token values are HTML-encoded by the renderer
/// so a name/title containing &lt;, &amp; or " can never break a branded email;
/// an explicit raw-HTML token set (bodyContent, *Html, *Block, dueText) passes
/// through verbatim so sender-built fragments still render. The Subject header
/// is plain text and is never encoded. Pure and offline.
/// </summary>
public class EmailTemplateRendererEncodingTests
{
    // A minimal layout: just drops the body in. The first "Subject:" line is
    // ignored by the renderer (the content template's subject wins).
    private const string Layout =
        "Subject: {{subject}}\n<div>{{bodyContent}}</div>";

    private static EmailTemplateRenderer NewRenderer() => new(Layout);

    private static Dictionary<string, string> Tokens(
        params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    // ----------------------------------------------------------------------
    // Plain tokens are encoded in the HTML body
    // ----------------------------------------------------------------------

    [Fact]
    public void Plain_token_value_with_markup_is_html_encoded_in_body()
    {
        var rendered = NewRenderer().Render(
            "Subject: hi\n<p>Hi {{firstName}}!</p>",
            Tokens(("firstName", "<script>alert('x')</script>")));

        // The raw markup must NOT appear in the body...
        Assert.DoesNotContain("<script>", rendered.HtmlBody);
        // ...it is encoded instead.
        Assert.Contains("&lt;script&gt;", rendered.HtmlBody);
    }

    [Fact]
    public void Ampersand_in_plain_token_is_encoded()
    {
        var rendered = NewRenderer().Render(
            "Subject: hi\n<p>{{companyName}}</p>",
            Tokens(("companyName", "Tom & Jerry <Co>")));

        Assert.Contains("Tom &amp; Jerry &lt;Co&gt;", rendered.HtmlBody);
        Assert.DoesNotContain("<Co>", rendered.HtmlBody);
    }

    [Fact]
    public void Double_quote_in_plain_token_used_in_attribute_is_encoded()
    {
        // A token value landing inside href="..." cannot break out of the
        // attribute because the quote is encoded.
        var rendered = NewRenderer().Render(
            "Subject: hi\n<a href=\"{{loginUrl}}\">go</a>",
            Tokens(("loginUrl", "https://x/\" onclick=\"evil()")));

        Assert.DoesNotContain("onclick=\"evil", rendered.HtmlBody);
        Assert.Contains("&quot;", rendered.HtmlBody);
    }

    // ----------------------------------------------------------------------
    // Raw-HTML tokens pass through verbatim
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData("leadListHtml")]   // *Html suffix
    [InlineData("taskListHtml")]   // *Html suffix
    [InlineData("messageHtml")]    // *Html suffix
    [InlineData("descriptionBlock")] // *Block suffix
    [InlineData("notesBlock")]     // *Block suffix
    [InlineData("dueText")]        // explicit member of the raw set
    public void Raw_html_token_is_not_re_encoded(string tokenName)
    {
        var html = "<strong>kept &amp; safe</strong>";
        var rendered = NewRenderer().Render(
            $"Subject: hi\n<div>{{{{{tokenName}}}}}</div>",
            Tokens((tokenName, html)));

        // The fragment is inserted verbatim (no double-encoding of its &amp;).
        Assert.Contains(html, rendered.HtmlBody);
        Assert.DoesNotContain("&amp;amp;", rendered.HtmlBody);
    }

    [Fact]
    public void BodyContent_passes_through_so_the_rendered_fragment_is_not_double_encoded()
    {
        // The content fragment is encoded once when substituted into the body,
        // then placed into the layout's {{bodyContent}} verbatim.
        var rendered = NewRenderer().Render(
            "Subject: hi\n<p>{{firstName}}</p>",
            Tokens(("firstName", "A & B")));

        Assert.Contains("<p>A &amp; B</p>", rendered.HtmlBody);
        Assert.DoesNotContain("&amp;amp;", rendered.HtmlBody);
    }

    // ----------------------------------------------------------------------
    // Subject header is plain text (never encoded)
    // ----------------------------------------------------------------------

    [Fact]
    public void Subject_header_is_plain_text_not_html_encoded()
    {
        var rendered = NewRenderer().Render(
            "Subject: Reminder: {{taskTitle}}\n<p>body</p>",
            Tokens(("taskTitle", "Pay R&D <invoice>")));

        // The email Subject header is a text header, not HTML — keep it literal.
        Assert.Equal("Reminder: Pay R&D <invoice>", rendered.Subject);
    }

    [Fact]
    public void Same_token_is_plain_in_subject_but_encoded_in_body()
    {
        var rendered = NewRenderer().Render(
            "Subject: {{taskTitle}}\n<p>{{taskTitle}}</p>",
            Tokens(("taskTitle", "A & B")));

        Assert.Equal("A & B", rendered.Subject);            // header: literal
        Assert.Contains("<p>A &amp; B</p>", rendered.HtmlBody); // body: encoded
    }

    // ----------------------------------------------------------------------
    // Missing tokens still collapse to empty
    // ----------------------------------------------------------------------

    [Fact]
    public void Missing_token_renders_as_empty_string()
    {
        var rendered = NewRenderer().Render(
            "Subject: hi\n<p>[{{nope}}]</p>",
            Tokens());

        Assert.Contains("<p>[]</p>", rendered.HtmlBody);
    }

    [Fact]
    public void Raw_html_token_set_membership_is_by_name_or_suffix()
    {
        // Documents the contract used by senders: a free-text token (e.g.
        // firstName) is encoded; a fragment token by suffix/name is raw.
        Assert.False(IsRaw("firstName"));
        Assert.False(IsRaw("companyName"));
        Assert.True(IsRaw("bodyContent"));
        Assert.True(IsRaw("dueText"));
        Assert.True(IsRaw("summaryHtml"));   // new *Html token is raw by default
        Assert.True(IsRaw("introBlock"));    // new *Block token is raw by default
    }

    // Mirror of EmailTemplateRenderer's internal rule, kept here so the test
    // documents the contract without reflecting into internals.
    private static bool IsRaw(string token) =>
        token is "bodyContent" or "dueText"
        || token.EndsWith("Html", System.StringComparison.Ordinal)
        || token.EndsWith("Block", System.StringComparison.Ordinal);
}
