using CommunityHub.Core.Integrations.Graphics;
using SixLabors.ImageSharp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §767 — the SoMe promotion graphics: layout contract, plus the example set the operator compares.
/// </summary>
/// <remarks>
/// <para>🔑 Two different jobs here, deliberately kept apart. The MECHANICAL contract — canvas size,
/// one GIF frame per speaker — regresses silently and is asserted. The LAYOUT cannot be asserted at
/// all: the only real check is a person looking at it, so <see cref="SoMeGraphicExampleService"/>
/// renders the full variant set into a folder for exactly that.</para>
///
/// <para>Asset-dependent cases SELF-SKIP when the template and photos are absent, so the suite stays
/// green on any machine and in CI without carrying a 4 MB photograph in the repo.</para>
/// </remarks>
public sealed class SoMeGraphicRendererTests
{
    /// <summary>
    /// Where the real template, speaker photos and sponsor logo live.
    /// </summary>
    /// <remarks>
    /// ⚠️ This pointed at ONE SESSION'S temp scratchpad, which is both session-specific and swept:
    /// the moment that folder went, every asset-dependent case here would have self-skipped in
    /// silence and the example set could not be re-rendered at all. It now points at the operator's
    /// own durable drop folder — the same place he delivers samples to — and is overridable so a
    /// different machine or CI can supply its own.
    /// </remarks>
    private static readonly string AssetFolder =
        Environment.GetEnvironmentVariable("CEH_GRAPHIC_ASSETS")
        ?? Path.Combine("C:", "tmp", "ceh", "graphics-samples", "eldk27-assets");

    private static readonly string ExampleFolder =
        Environment.GetEnvironmentVariable("CEH_GRAPHIC_EXAMPLES")
        ?? Path.Combine("C:", "tmp", "ceh", "graphics-samples", "eldk27-examples");

    private static string? Asset(string fileName)
    {
        var p = Path.Combine(AssetFolder, fileName);
        return File.Exists(p) ? p : null;
    }

    private static byte[] SolidImage(int w, int h)
    {
        using var img = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public void A_speaker_graphic_is_always_the_social_card_size()
    {
        // Synthetic inputs — this contract must hold without any real asset.
        var png = new SoMeGraphicRenderer()
            .RenderSpeakerPng(SolidImage(2000, 1333), SolidImage(400, 400), "Ada Lovelace");

        using var img = Image.Load(png);
        Assert.Equal(SoMeGraphicRenderer.Width, img.Width);
        Assert.Equal(SoMeGraphicRenderer.Height, img.Height);
    }

    [Fact]
    public void A_speaker_with_no_photo_still_renders()
    {
        // A speaker who has not supplied a headshot must not break the run — the badge renders empty
        // and the name still carries the post.
        var png = new SoMeGraphicRenderer()
            .RenderSpeakerPng(SolidImage(2000, 1333), null, "Grace Hopper");

        using var img = Image.Load(png);
        Assert.Equal(SoMeGraphicRenderer.Width, img.Width);
    }

    [Fact]
    public void A_gif_carries_ONE_FRAME_PER_SPEAKER()
    {
        // 🔒 The whole PNG-vs-GIF rule rests on this: a GIF is not a different design, it is the
        // same layout repeated per person.
        var template = SolidImage(2000, 1333);
        var face = SolidImage(400, 400);

        var gif = new SoMeGraphicRenderer().RenderSpeakerGif(
            template,
            new (byte[]?, string)[] { (face, "One"), (face, "Two"), (face, "Three") });

        using var img = Image.Load(gif);
        Assert.Equal(3, img.Frames.Count);
    }

    [Fact]
    public void A_gif_needs_at_least_one_speaker()
    {
        Assert.Throws<ArgumentException>(() =>
            new SoMeGraphicRenderer().RenderSpeakerGif(SolidImage(200, 133), Array.Empty<(byte[]?, string)>()));
    }

    [Fact]
    public void A_sponsor_bundle_carries_ONE_FRAME_PER_SPONSOR()
    {
        // §767 phase 1: a category bundle is not a different design — it is the single-sponsor
        // layout repeated per member, exactly as the speaker GIF is.
        var gif = new SoMeGraphicRenderer().RenderSponsorGif(
            SolidImage(2000, 1333),
            new[] { SolidImage(300, 120), SolidImage(300, 120), SolidImage(300, 120) },
            "Platinum sponsors");

        using var img = Image.Load(gif);
        Assert.Equal(3, img.Frames.Count);
        Assert.Equal(SoMeGraphicRenderer.Width, img.Width);
    }

    [Fact]
    public void A_sponsor_bundle_needs_at_least_one_sponsor()
    {
        Assert.Throws<ArgumentException>(() =>
            new SoMeGraphicRenderer().RenderSponsorGif(SolidImage(200, 133), Array.Empty<byte[]>()));
    }

    [Fact]
    public void A_sponsor_bundle_does_not_leak_its_caption_into_later_renders()
    {
        // The bundle sets SponsorCaption for the duration of the render. If it failed to put the
        // previous value back, every later single-sponsor graphic from the same renderer would
        // silently inherit "Platinum sponsors" — wrong words on the right picture, which no test of
        // frame COUNT would ever catch.
        var renderer = new SoMeGraphicRenderer { SponsorCaption = "Gold sponsor" };
        renderer.RenderSponsorGif(
            SolidImage(2000, 1333), new[] { SolidImage(300, 120) }, "Platinum sponsors");

        Assert.Equal("Gold sponsor", renderer.SponsorCaption);
    }

    [Fact]
    public void The_locked_design_is_the_approved_one()
    {
        // 🔒 These are the operator's decisions (§767), not defaults. A silent change here would
        // change every graphic ELDK publishes — the kind of drift nobody notices until two posts
        // sit side by side.
        var locked = SoMeGraphicRenderer.LockedDesign();

        Assert.True(locked.TwoToneRing);
        Assert.True(locked.WhiteEventLogo);
        Assert.True(locked.SponsorEventLogo);
        Assert.Equal(0.56f, locked.CropZoom);
        Assert.Equal(0f, locked.CropAnchorX);
    }

    [Fact]
    public void A_sponsor_logo_resolves_to_the_NEWEST_uploaded_version()
    {
        // 🔒 Pins the naming the SPONSOR UPLOAD PATH actually writes: §768.8's
        // {sponsor}-logo-web-{N}.png in Sponsors/Logo/Web (SponsorUploadKinds, kind "some").
        // Matching the OTHER convention — the collection folder's "{Company} - {file}" — found
        // nothing at all, silently, which is the worst possible failure: every bundle simply had no
        // logos and still "succeeded".
        //
        // 🔑 And the version is the change signal: a re-upload writes {N+1} rather than overwriting,
        // so newest-wins is what makes a replaced logo reach the artwork.
        //
        // ⚠️ §768.14 — this test FAILED when the writers moved to the new convention, which is
        // exactly what it is for. Both sides now go through SponsorUploadNaming, so a future change
        // to the contract breaks the build rather than quietly emptying the sponsor line-up.
        var files = new[]
        {
            new SharePointFileRef("1", "2linkit-logo-web-1.png", ""),
            new SharePointFileRef("3", "2linkit-logo-web-3.png", ""),
            new SharePointFileRef("2", "2linkit-logo-web-2.png", ""),
            new SharePointFileRef("x", "otherco-logo-web-9.png", ""),
        };

        var newest = SoMeBundleBuildService.ResolveNewestSponsorLogo(files, "2LINKIT");
        Assert.NotNull(newest);
        Assert.Equal("2linkit-logo-web-3.png", newest!.Name);

        // A company with no upload yet must return null, not somebody else's logo.
        Assert.Null(SoMeBundleBuildService.ResolveNewestSponsorLogo(files, "Nobody A/S"));

        // A PRINT logo in the same listing must not be mistaken for the web one — the kind is part
        // of the NAME, not merely part of the folder.
        Assert.Null(SoMeBundleBuildService.ResolveNewestSponsorLogo(
            new[] { new SharePointFileRef("p", "2linkit-logo-print-4.png", "") }, "2LINKIT"));

        // 🔒 The retired _v{N} form must NOT match. Old files may sit beside new ones after the
        // §768 fresh start; picking one up would put a superseded logo on live artwork.
        Assert.Null(SoMeBundleBuildService.ResolveNewestSponsorLogo(
            new[] { new SharePointFileRef("o", "SoMeBrandingLogo_2LINKIT_v9.png", "") }, "2LINKIT"));
    }

    [Fact]
    public void A_sponsor_name_with_hyphens_still_resolves()
    {
        // 🔒 §768.14 — the parser peels the version and the kind off the RIGHT precisely so a
        // hyphenated company survives. The pre-§768.14 heuristic split at the FIRST underscore and
        // treated whatever preceded it as an upload prefix to discard, so a name carrying the
        // separator lost its logo — silently, like every miss on this path.
        var files = new[] { new SharePointFileRef("1", "acme-corp-a-s-logo-web-2.png", "") };

        var newest = SoMeBundleBuildService.ResolveNewestSponsorLogo(files, "Acme Corp A/S");

        Assert.NotNull(newest);
        Assert.Equal("acme-corp-a-s-logo-web-2.png", newest!.Name);
    }

    [Fact]
    public void A_bundle_key_separates_tier_from_type()
    {
        // ⚠️ Tier and type are two DIFFERENT groupings over the same sponsors. Without the kind in
        // the key, a tier and a type sharing a name would collide onto one file and one bundle
        // would vanish with nothing to show it had.
        Assert.NotEqual(
            CommunityHub.Core.Domain.GraphicStableKey.ForSponsorCategory("tier", "gold"),
            CommunityHub.Core.Domain.GraphicStableKey.ForSponsorCategory("type", "gold"));
    }

    [Fact]
    public void A_gif_bundle_is_stored_with_a_gif_extension()
    {
        // The PNG-vs-GIF rule has to reach the FILE, not just the bytes: a GIF written as .png is
        // what a "works on my machine" render looks like right up until a platform refuses it.
        Assert.EndsWith(".gif", CommunityHub.Core.Domain.GraphicStableKey.FileName(
            CommunityHub.Core.Domain.GraphicStableKey.ForTrackGraphic("security"), ".gif"));
        Assert.EndsWith(".png", CommunityHub.Core.Domain.GraphicStableKey.FileName(
            CommunityHub.Core.Domain.GraphicStableKey.ForSpeaker(42)));
    }

    /// <summary>
    /// Renders the whole variant set from the REAL template and photos, for the operator to compare.
    /// Self-skips when those assets are not present.
    /// </summary>
    [Fact]
    public void Render_the_example_set_for_review()
    {
        var template = Asset("Template.jpg");
        var photo = Asset("speaker1.png");
        var photo2 = Asset("speaker2.jpg");
        var logo = Asset("sponsor-logo.png");
        if (template is null || photo is null || photo2 is null || logo is null) return;

        var written = new SoMeGraphicExampleService().WriteTo(
            ExampleFolder,
            File.ReadAllBytes(template), File.ReadAllBytes(photo),
            File.ReadAllBytes(photo2), File.ReadAllBytes(logo));

        Assert.True(written.Count >= 32);
        Assert.All(written, p => Assert.True(new FileInfo(p).Length > 0));
    }
}
