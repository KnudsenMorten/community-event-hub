using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// The SoMe-graphics COMPOSITING engine (REQUIREMENTS §18). Pure
/// SixLabors.ImageSharp — cross-platform, no System.Drawing / libgdiplus. Merges a
/// foreground image (a speaker PHOTO or a sponsor LOGO) plus a text label (the
/// speaker NAME) onto a template BACKGROUND PNG and returns the composited PNG
/// BYTES. No SharePoint / DB concern — that is the service's job; this is the
/// deterministic, offline-testable image step.
///
/// Degrades gracefully: if no font is resolvable on the host (e.g. a bare Linux
/// container) the image still composites WITHOUT the text overlay rather than
/// throwing — a graphic is always produced.
/// </summary>
public sealed class GraphicCompositor
{
    /// <summary>PNG content type for the produced bytes.</summary>
    public const string PngContentType = "image/png";

    /// <summary>
    /// Compose a speaker graphic: the template background with the speaker photo
    /// placed (centered in the lower portion by default) and the speaker name drawn
    /// beneath it. Returns PNG bytes. <paramref name="photoPng"/> may be null
    /// (e.g. no Sessionize picture yet) — then only the background + name render.
    /// </summary>
    public byte[] ComposeSpeakerGraphic(
        byte[] templatePng, byte[]? photoPng, string speakerName)
    {
        using var background = Image.Load<Rgba32>(templatePng);

        if (photoPng is not null && photoPng.Length > 0)
        {
            using var photo = Image.Load<Rgba32>(photoPng);
            // Place the photo in a square ~45% of the background width, centered
            // horizontally, in the upper-middle band.
            var side = Math.Max(1, (int)(background.Width * 0.45));
            photo.Mutate(c => c.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Crop,
                Size = new Size(side, side),
            }));
            var px = (background.Width - side) / 2;
            var py = (int)(background.Height * 0.12);
            background.Mutate(c => c.DrawImage(photo, new Point(px, py), 1f));
        }

        DrawCenteredText(background, speakerName, yFraction: 0.78f);
        return ToPng(background);
    }

    /// <summary>
    /// Compose a sponsor graphic: the template background with the sponsor logo
    /// placed (centered). INTERNAL-ONLY usage is enforced by the service / queries,
    /// not here. Returns PNG bytes.
    /// </summary>
    public byte[] ComposeSponsorGraphic(byte[] templatePng, byte[] logoPng)
    {
        using var background = Image.Load<Rgba32>(templatePng);
        using var logo = Image.Load<Rgba32>(logoPng);

        // Fit the logo inside ~60% width / 40% height, preserving aspect, centered.
        var maxW = (int)(background.Width * 0.60);
        var maxH = (int)(background.Height * 0.40);
        logo.Mutate(c => c.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(maxW, maxH),
        }));
        var px = (background.Width - logo.Width) / 2;
        var py = (background.Height - logo.Height) / 2;
        background.Mutate(c => c.DrawImage(logo, new Point(px, py), 1f));

        return ToPng(background);
    }

    /// <summary>
    /// Compose a per-session graphic: speaker photo + the session title under the
    /// speaker name. Same layout as the speaker graphic with an extra title line.
    /// </summary>
    public byte[] ComposeSessionGraphic(
        byte[] templatePng, byte[]? photoPng, string speakerName, string sessionTitle)
    {
        using var background = Image.Load<Rgba32>(templatePng);

        if (photoPng is not null && photoPng.Length > 0)
        {
            using var photo = Image.Load<Rgba32>(photoPng);
            var side = Math.Max(1, (int)(background.Width * 0.40));
            photo.Mutate(c => c.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Crop,
                Size = new Size(side, side),
            }));
            var px = (background.Width - side) / 2;
            var py = (int)(background.Height * 0.08);
            background.Mutate(c => c.DrawImage(photo, new Point(px, py), 1f));
        }

        DrawCenteredText(background, speakerName, yFraction: 0.62f);
        DrawCenteredText(background, sessionTitle, yFraction: 0.78f, relativeSize: 0.7f);
        return ToPng(background);
    }

    // ----- internals -------------------------------------------------------

    private static byte[] ToPng(Image image)
    {
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Draw <paramref name="text"/> centered horizontally at the given vertical
    /// fraction of the image, in white. Degrades to a no-op (no throw) when no font
    /// can be resolved on the host.
    /// </summary>
    private static void DrawCenteredText(
        Image<Rgba32> image, string? text, float yFraction, float relativeSize = 1.0f)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var font = ResolveFont((int)(image.Width * 0.06 * relativeSize));
        if (font is null) return; // no font on host — skip text, still produce an image

        var options = new RichTextOptions(font)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Origin = new PointF(image.Width / 2f, image.Height * yFraction),
            WrappingLength = image.Width * 0.9f,
        };

        image.Mutate(c => c.DrawText(options, text, Color.White));
    }

    /// <summary>
    /// Resolve a usable font: the first available system font (any family). Returns
    /// null when the host has no fonts installed — the compositor then skips text.
    /// </summary>
    private static Font? ResolveFont(int size)
    {
        if (size < 1) size = 1;
        var families = SystemFonts.Families.ToList();
        if (families.Count == 0) return null;

        // Prefer a common sans-serif if present; otherwise the first family.
        var preferred = families.FirstOrDefault(f =>
            f.Name.Contains("Arial", StringComparison.OrdinalIgnoreCase)
            || f.Name.Contains("DejaVu", StringComparison.OrdinalIgnoreCase)
            || f.Name.Contains("Segoe", StringComparison.OrdinalIgnoreCase)
            || f.Name.Contains("Liberation", StringComparison.OrdinalIgnoreCase));
        var family = preferred.Name is not null ? preferred : families[0];
        return family.CreateFont(size, FontStyle.Bold);
    }
}
