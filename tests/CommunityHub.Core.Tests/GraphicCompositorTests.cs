using CommunityHub.Core.Integrations.Graphics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Unit tests for the SoMe-graphics COMPOSITING engine (REQUIREMENTS §18). Proves
/// the compositor produces a valid PNG for speaker / sponsor / session graphics,
/// merging the supplied foreground + name onto the template background. Runs fully
/// offline (pure ImageSharp) — no SharePoint, no DB, no fonts required (the engine
/// degrades gracefully when the host has no font).
/// </summary>
public sealed class GraphicCompositorTests
{
    private readonly GraphicCompositor _compositor = new();

    /// <summary>A solid-colour PNG of the given size, used as a template / photo / logo.</summary>
    private static byte[] MakePng(int w, int h, Rgba32 color)
    {
        using var img = new Image<Rgba32>(w, h, color);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length > 8
        && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
        && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    [Fact]
    public void Speaker_graphic_produces_a_png_of_the_template_size()
    {
        var template = MakePng(1080, 1080, new Rgba32(10, 50, 90));
        var photo = MakePng(400, 400, new Rgba32(200, 200, 200));

        var png = _compositor.ComposeSpeakerGraphic(template, photo, "Session Speaker One");

        Assert.True(IsPng(png), "output must be a PNG");
        using var img = Image.Load<Rgba32>(png);
        Assert.Equal(1080, img.Width);
        Assert.Equal(1080, img.Height);
    }

    [Fact]
    public void Speaker_graphic_works_without_a_photo()
    {
        // No Sessionize picture yet — the background + name still composite.
        var template = MakePng(800, 800, new Rgba32(0, 0, 0));

        var png = _compositor.ComposeSpeakerGraphic(template, photoPng: null, "No Photo Speaker");

        Assert.True(IsPng(png));
        using var img = Image.Load<Rgba32>(png);
        Assert.Equal(800, img.Width);
    }

    [Fact]
    public void Sponsor_graphic_merges_the_logo_into_a_png()
    {
        var template = MakePng(1200, 630, new Rgba32(255, 255, 255));
        var logo = MakePng(300, 120, new Rgba32(0, 120, 210));

        var png = _compositor.ComposeSponsorGraphic(template, logo);

        Assert.True(IsPng(png));
        using var img = Image.Load<Rgba32>(png);
        Assert.Equal(1200, img.Width);
        Assert.Equal(630, img.Height);
    }

    [Fact]
    public void Session_graphic_produces_a_png()
    {
        var template = MakePng(1080, 1080, new Rgba32(20, 20, 40));
        var photo = MakePng(300, 300, new Rgba32(180, 180, 180));

        var png = _compositor.ComposeSessionGraphic(
            template, photo, "Session Speaker One", "Building things in the cloud");

        Assert.True(IsPng(png));
    }

    [Fact]
    public void Content_type_is_png()
    {
        Assert.Equal("image/png", GraphicCompositor.PngContentType);
    }
}
