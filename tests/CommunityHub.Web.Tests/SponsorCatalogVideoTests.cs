using CommunityHub.Pages;
using Markdig;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1312 — the sponsor catalogue plays a page's video IN the page and shows a slide's pictures side by side with captions.
/// Operator 2026-09-27: "some pics are not shown correctly and video doesnt autoplay correctly".
/// </summary>
public sealed class SponsorCatalogVideoTests
{
    [Theory]
    [InlineData("https://youtu.be/DT086UssZu4", "https://www.youtube-nocookie.com/embed/DT086UssZu4?rel=0")]
    [InlineData("https://www.youtube.com/watch?v=q-Ytkmze8CM&t=3", "https://www.youtube-nocookie.com/embed/q-Ytkmze8CM?rel=0")]
    [InlineData("https://www.youtube.com/shorts/DT086UssZu4", "https://www.youtube-nocookie.com/embed/DT086UssZu4?rel=0")]
    [InlineData("https://vimeo.com/123456789", "https://player.vimeo.com/video/123456789")]
    [InlineData("https://highlights.eldk26.video.example.test", null)]   // an unresolved short link stays a plain link
    [InlineData("https://youtu.be/bad\"id", null)]
    [InlineData(null, null)]
    public void A_video_link_becomes_an_in_page_player(string? url, string? embed) =>
        Assert.Equal(embed, SponsorCatalogModel.EmbedUrl(url));

    [Fact]
    public void An_imported_gallery_renders_as_figures_with_captions()
    {
        // The same pipeline as the catalogue page (advanced extensions, HTML disabled).
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
        var html = Markdown.ToHtml("::: catalog-gallery\n^^^\n![Staff](/shop-catalog-image/catalog-a-1.jpg)\n^^^ Staff tickets\n\n^^^\n![TV](/shop-catalog-image/catalog-a-2.jpg)\n^^^\n\n:::", pipeline);

        Assert.Contains("<div class=\"catalog-gallery\">", html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "<figure>").Count);
        Assert.Contains("<figcaption>Staff tickets</figcaption>", html);
    }
}
