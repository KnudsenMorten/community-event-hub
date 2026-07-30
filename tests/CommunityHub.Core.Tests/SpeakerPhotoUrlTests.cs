using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §665 — the speaker photo must resolve to a URL a BROWSER can fetch.
///
/// <para>The bug: the sponsor upload stored the SharePoint document URL in
/// <c>SpeakerProfile.PhotoUrl</c>. A browser has no SharePoint credentials, so the request lands on
/// a sign-in page and the image renders broken — next to a confident "Photo on file". Re-uploading
/// could not fix it, because every upload wrote another equally unfetchable URL.</para>
/// </summary>
public class SpeakerPhotoUrlTests
{
    [Fact]
    public void A_sharepoint_backed_photo_resolves_to_the_hub_proxy()
    {
        var url = SpeakerPhotoUrl.Resolve(
            "https://contoso.sharepoint.com/sites/x/Shared%20Documents/Speakers/speaker-photo-a-7.jpg",
            "speaker-photo-a-7.jpg");

        Assert.Equal("/speaker-photo/speaker-photo-a-7.jpg", url);
    }

    [Fact]
    public void An_existing_broken_row_repairs_itself_from_the_sharepoint_path()
    {
        // The rows saved BEFORE the fix: a sharepoint.com PhotoUrl AND a PhotoSharePointPath.
        // The path wins, so they start working with no data migration.
        var url = SpeakerPhotoUrl.Resolve(
            "https://contoso.sharepoint.com/sites/x/Speakers/old.png", "old.png");

        Assert.Equal("/speaker-photo/old.png", url);
    }

    [Fact]
    public void A_sharepoint_url_with_NO_stored_path_resolves_to_nothing()
    {
        // Nothing can be served, so the honest answer is "no photo". Returning the SharePoint URL
        // would render the broken glyph this whole change exists to remove.
        Assert.Null(SpeakerPhotoUrl.Resolve(
            "https://contoso.sharepoint.com/sites/x/Speakers/gone.jpg", null));
    }

    [Fact]
    public void A_public_sessionize_photo_is_left_exactly_as_it_is()
    {
        // This is why the bug hid for so long: imported photos are public URLs and always worked.
        const string sessionize = "https://sessionize.com/image/abc-400o400o1-xyz.jpg";
        Assert.Equal(sessionize, SpeakerPhotoUrl.Resolve(sessionize, null));
    }

    [Fact]
    public void No_photo_at_all_resolves_to_null()
    {
        Assert.Null(SpeakerPhotoUrl.Resolve(null, null));
        Assert.Null(SpeakerPhotoUrl.Resolve("   ", "   "));
    }

    [Theory]
    [InlineData("speaker-photo-a-7.jpg", "speaker-photo-a-7.jpg")]
    [InlineData("Speakers/speaker-photo-a-7.PNG", "speaker-photo-a-7.PNG")]   // sub-path stripped
    [InlineData("Speakers\\nested\\p.webp", "p.webp")]                        // windows separator
    [InlineData("../../secrets/p.jpg", null)]                                 // traversal refused
    [InlineData("notes.txt", null)]                                           // not an image
    [InlineData("payload.svg", null)]                                         // not in the allowlist
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SafeLeaf_reduces_to_a_servable_image_leaf_or_nothing(string? stored, string? expected)
    {
        Assert.Equal(expected, SpeakerPhotoService.SafeLeaf(stored));
    }

    [Fact]
    public void A_name_needing_escaping_is_url_encoded()
    {
        // A speaker name with a space must not produce a URL the browser splits.
        var url = SpeakerPhotoUrl.Resolve(null, "speaker photo 7.jpg");
        Assert.Equal("/speaker-photo/speaker%20photo%207.jpg", url);
    }
}
