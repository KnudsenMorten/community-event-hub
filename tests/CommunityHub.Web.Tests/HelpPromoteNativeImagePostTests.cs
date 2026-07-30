using System;
using System.IO;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §437 (operator 2026-07-27): <i>"the linkedin post is still not perfect. i want the post to be
/// an image like this - but instead it becomes a url-based image and not a picture … Or do we need
/// to download image to disk + manually attach as image to the post"</i>.
///
/// <para>For the speaker's OWN profile the answer is his second guess, and it is a platform limit,
/// not a bug: a LinkedIn share LINK carries plain text only (§326ac), so any URL in it is unfurled
/// into a small preview CARD — that card IS the "url-based image" he saw. A native, full-width
/// image needs the Images API with the AUTHOR's token, which for a member post means the app
/// consent ruled out in §326w. (The EVENT PAGE post is a different path and already uploads
/// natively — <c>LiveLinkedInPostPublisher.UploadImageAsync</c>.)</para>
///
/// <para>So the manual route was promoted from footnote to PRIMARY button. These pin the parts
/// that make it work, all of which are silent if broken:</para>
/// <list type="number">
/// <item>the picture button opens an EMPTY composer — a <c>url=</c>/<c>text=</c> param would put
///   the preview card straight back;</item>
/// <item>it carries the graphic download URL, so the click actually saves the PNG;</item>
/// <item>the link-card route survives as the SECONDARY button (one click, thumbnail).</item>
/// </list>
/// Static check over the Razor source — the same approach as the other wiring tests, no host.
/// </summary>
public sealed class HelpPromoteNativeImagePostTests
{
    private static string HelpPromotePage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "CommunityHub", "Pages", "Speaker", "Graphics.cshtml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate src/CommunityHub/Pages/Speaker/Graphics.cshtml from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_picture_button_opens_an_EMPTY_composer_with_nothing_to_unfurl()
    {
        // The constant the page renders — nothing after the composer path.
        Assert.Equal(
            "https://www.linkedin.com/feed/?shareActive=true",
            CommunityHub.Pages.Speaker.GraphicsModel.EmptyComposerUrl);

        // A url=/text= param is exactly what re-creates the link-preview card the operator
        // rejected, so the empty composer must stay empty.
        Assert.DoesNotContain("url=", CommunityHub.Pages.Speaker.GraphicsModel.EmptyComposerUrl);
        Assert.DoesNotContain("&text=", CommunityHub.Pages.Speaker.GraphicsModel.EmptyComposerUrl);
    }

    [Fact]
    public void The_page_offers_the_picture_route_first_and_downloads_the_png()
    {
        var page = HelpPromotePage();

        // The primary button, wired to the empty composer and to the PNG proxy.
        // §663 (operator 2026-07-29, mock-up): the two routes are now two CARDS. The choice is
        // stated by a badge + a one-line trade-off inside each card, which replaced both the
        // §441 "preferred / alternative" labels AND the explanatory paragraph above the row.
        Assert.Contains("js-share-image", page);
        Assert.Contains("GraphicsModel.EmptyComposerUrl", page);
        Assert.Contains("LinkedIn: Post with graphic", page);
        Assert.Contains("Full-width image. Requires two manual steps in LinkedIn.", page);
        Assert.Contains("li-choice__badge--rec", page);
        // §454 (operator 2026-07-27): the trailing "(see below)" is gone — the steps appear
        // right under the button when it is clicked, so the pointer was redundant.
        Assert.DoesNotContain("image/picture (see below)", page);

        // The handler must actually download — a button that only opens LinkedIn would
        // leave the speaker with nothing to attach.
        Assert.Contains("setAttribute('download'", page);

        // The link-card route stays, as the "Fastest" card.
        Assert.Contains("LinkedIn: Post with link", page);
        Assert.Contains("Fully automatic. Thumbnail image is small.", page);
        Assert.Contains("li-choice__badge--fast", page);
        Assert.Contains("js-share-intent", page);

        // §662 — the alternative button no longer renders in its own dark navy: the badges
        // carry the distinction now, so both buttons are LinkedIn blue. This was his literal
        // ask ("change color of this to same as the others").
        Assert.Contains(".btn-linkcard { background:#0a66c2", page);
        Assert.DoesNotContain("#1D3380; color:#fff; border:0", page);
    }

    /// <summary>
    /// §662 — the OTHER half of that report: <i>"when I move move over it blanks the color so i
    /// cannot read text"</i>.
    /// </summary>
    /// <remarks>
    /// The cause was never in this page's palette. <c>_LayoutStyles</c> carries a global
    /// <c>a:hover { color: var(--el-darkblue); }</c>, and an element+pseudo-class selector
    /// out-specifies a plain class — so hovering any <c>&lt;a class="btn"&gt;</c> replaced the white
    /// label with dark blue, which on the dark button is invisible. Every hover/focus rule in the
    /// row must therefore restate the colour, not just the background.
    /// </remarks>
    [Fact]
    public void Every_button_hover_state_keeps_a_readable_label()
    {
        var page = HelpPromotePage();

        foreach (var rule in new[] { ".btn:hover", ".btn-publish:hover", ".btn-linkcard:hover" })
        {
            var at = page.IndexOf(rule, StringComparison.Ordinal);
            Assert.True(at >= 0, $"hover rule '{rule}' not found on the Help Promote page");

            var block = page[at..page.IndexOf('}', at)];
            Assert.True(
                block.Contains("color:#fff", StringComparison.OrdinalIgnoreCase),
                $"'{rule}' changes the background without restating the label colour — the global "
                + "a:hover rule will win and the text becomes unreadable mid-click (§662).");
        }
    }

    [Fact]
    public void The_speaker_is_told_the_two_things_only_they_can_do()
    {
        var page = HelpPromotePage();
        // Attaching the file is the step that turns the thumbnail into a real picture; and
        // the page tag can only be inserted from inside the composer (§326ac).
        Assert.Contains("photo", page);
        Assert.Contains("Paste your post text", page);
        Assert.Contains("full-width picture instead of a small link preview", page);
    }
}
