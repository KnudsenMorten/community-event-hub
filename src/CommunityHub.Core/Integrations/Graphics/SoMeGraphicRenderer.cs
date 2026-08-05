using System.Numerics;
using System.Reflection;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// §767 — renders the SoMe promotion graphics: the ELDK layout drawn onto the event template.
/// </summary>
/// <remarks>
/// <para>🔑 <b>The decorative layers are DRAWN, not supplied</b> (operator 2026-08-01: <i>"the
/// graphics service should add these 'layers' (frame, ring for faces + box with logo for
/// sponsors)"</i>). The template is a plain event photograph; everything else — the tone-down, the
/// white frame, the circular speaker ring with its curved caption, the sponsor panel — is composed
/// here. The ring therefore follows the brand palette instead of being a PNG somebody redraws each
/// edition.</para>
///
/// <para>🔒 <b>Geometry is MEASURED from the ELDK26 output, not invented</b> — canvas 1200×627,
/// frame inset 17px, ring centre (887, 329) radius 249. Those numbers are constants below so the
/// new material sits where the old material sat.</para>
///
/// <para>🔒 <b>Fonts are the EMBEDDED Open Sans, never <c>SystemFonts</c>.</b> §750.7: Linux App
/// Service has no system fonts at all, and the older <c>GraphicCompositor</c> silently skips its text
/// when none resolves — so on Azure it produces graphics with no name on them and no error. This
/// renderer fails loudly instead: a missing font is a broken graphic, not a quiet one.</para>
///
/// <para>Pure: bytes in, bytes out. No SharePoint, no DB, no clock — so the whole layout is testable
/// offline and a sample can be rendered and looked at.</para>
/// </remarks>
public sealed class SoMeGraphicRenderer
{
    // ---- canvas + layers, measured from the ELDK26 samples ----------------------------
    public const int Width = 1200;
    public const int Height = 627;

    private const int FrameInset = 17;
    private const float FrameThickness = 2f;

    private const float RingCentreX = 887f;
    private const float RingCentreY = 329f;
    private const float RingOuterRadius = 249f;

    /// <summary>Thickness of the blue ring band that carries the caption.</summary>
    private const float RingBandWidth = 46f;

    /// <summary>The white gap between the blue band and the photo.</summary>
    private const float PhotoGap = 8f;

    /// <summary>Sponsor panel bounds. The panel is sized to its logo, but never outside these.</summary>
    private const float SponsorPanelMargin = 45f;
    private const float SponsorPanelTop = 200f;
    private const float SponsorPanelMaxHeight = 230f;

    private const string RingCaption = "WHERE THE MICROSOFT COMMUNITY MEETS";
    private const string BadgeWord = "SPEAKER";

    // Experts Live palette (§754 §6).
    private static readonly Color BrandBlue = Color.ParseHex("008BD2");
    private static readonly Color BrandDarkBlue = Color.ParseHex("1D3380");

    /// <summary>
    /// How far the background is darkened, 0–1. The template ships full-brightness, so the
    /// tone-down is a LAYER like the others — tunable without re-exporting a 4 MB photograph.
    /// </summary>
    public float ToneDown { get; set; } = 0.45f;

    /// <summary>
    /// Vertical crop anchor, 0 = top .. 1 = bottom. The template is 3:2 and the output is 1.91:1, so
    /// ~22% of the height is discarded; which 22% is a decision, not a default (§767).
    /// </summary>
    public float CropAnchor { get; set; } = 0.5f;

    /// <summary>
    /// How much of the source WIDTH to use, 0.3–1.0. Below 1 the crop zooms in, which is what makes
    /// a horizontal crop possible at all.
    /// </summary>
    /// <remarks>
    /// 🔑 At 1.0 the output ratio is wider than the source, so the crop already consumes the FULL
    /// width and there is no horizontal slack to move around — trimming anything off the sides means
    /// taking a narrower window and scaling it up. The source is 4000 px wide against a 1200 px
    /// output, so even 0.6 still resamples DOWN and costs no sharpness.
    /// </remarks>
    public float CropZoom { get; set; } = 1.0f;

    /// <summary>Horizontal crop anchor, 0 = left .. 1 = right. Only bites when <see cref="CropZoom"/> &lt; 1.</summary>
    /// <remarks>
    /// 🔒 Operator 2026-08-01, choosing between three options: <i>"yes do the horizontal crop"</i>.
    /// The purpose is to exclude the stage screens on the RIGHT of the template, which still read
    /// "Welcome to ELDK26" / "#ELDK26" / 2026 — visible beside the sponsor panel once it was narrowed
    /// to fit its logo. He keeps last year's photograph deliberately; this removes last year's TEXT
    /// from it, which is the part that is actually wrong on ELDK27 material.
    /// <para>This is the only fix that works for EVERY layout at once — widening the sponsor panel
    /// would have hidden the screens only where a panel happens to sit.</para>
    /// </remarks>
    public float CropAnchorX { get; set; } = 0.5f;

    // ---- variant knobs -----------------------------------------------------------------
    // §767: he has never automated this before ("last year a graphic person made them manually")
    // and expects to iterate, so the things most likely to be argued about are PROPERTIES rather
    // than constants — variants can then be rendered side by side without editing the renderer.

    /// <summary>Give the ring a darker outer rim, as the ELDK26 badge has.</summary>
    public bool TwoToneRing { get; set; }

    /// <summary>
    /// The event mark to stamp on the graphic, as supplied from the <b>Graphics/Template</b> folder
    /// beside the template photograph.
    /// </summary>
    /// <remarks>
    /// 🔒 Operator 2026-08-01: <i>"try example with our white logo instead. not blue"</i> → <i>"just
    /// put it in template folder"</i>. So the white mark is an ASSET HE OWNS, sitting next to the
    /// template, and it is replaced by dropping a new file there — not by a deploy. The ELDK26
    /// measurement agrees ("white knockout logo top-left").
    /// <para>Null ⇒ falls back to <see cref="WhiteEventLogo"/> over the embedded colour mark, so a
    /// missing file degrades to something correct-looking rather than to a blue mark nobody can read
    /// on a dark photograph.</para>
    /// </remarks>
    public byte[]? EventLogo { get; set; }

    /// <summary>
    /// Fallback only: knock the embedded COLOUR mark out to white when no logo file was supplied.
    /// </summary>
    /// <remarks>
    /// The embedded asset is the colour version, whose dark-blue "Experts" all but disappears
    /// against a toned-down photograph. Derived from the alpha channel so no second asset has to be
    /// kept in sync with the mark the evaluation report uses (§750.8).
    /// </remarks>
    public bool WhiteEventLogo { get; set; } = true;

    /// <summary>Mirror the layout: badge on the left, name on the right.</summary>
    public bool BadgeOnLeft { get; set; }

    /// <summary>Optional second line under the name — a session or master-class title.</summary>
    public string? Subtitle { get; set; }

    /// <summary>Optional caption over the sponsor panel, e.g. "PLATINUM SPONSOR".</summary>
    public string? SponsorCaption { get; set; }

    /// <summary>
    /// Put the Experts Live Denmark mark centred above the sponsor panel, as the ELDK26 sponsor
    /// layout has it. Off by default only because the delivered example set was rendered without it
    /// (§767) — the measured reference DOES carry the mark, so this is expected to become the default
    /// once the operator has compared C1 against C4.
    /// </summary>
    public bool SponsorEventLogo { get; set; }

    /// <summary>The word on the badge plate. "SPEAKER" for talks; overridable for other roles.</summary>
    public string BadgeLabel { get; set; } = "SPEAKER";

    /// <summary>Event name for the strip along the bottom, e.g. "Experts Live Denmark 2027".</summary>
    /// <remarks>
    /// Operator 2026-08-01: <i>"can we test adding dates and event name"</i>. Drawn along the BOTTOM
    /// rather than beside the name: the name column is already carrying a wrapped name plus a
    /// session title, and a post is scanned top-down — who and what first, when and where after.
    /// 🔑 Both values come from the event context that every other surface already uses
    /// (<c>BrandingEventContext</c>), never typed into the renderer, so an edition change moves them
    /// everywhere at once.
    /// </remarks>
    public string? EventName { get; set; }

    /// <summary>Human-readable date range for the bottom strip, e.g. "9-10 Feb 2027".</summary>
    public string? EventDates { get; set; }

    /// <summary>Where it happens, e.g. "Copenhagen, Denmark" — the third element of the strip.</summary>
    /// <remarks>
    /// Operator 2026-08-01: <i>"add Denmark also"</i>, then <i>"what makes sense"</i>. It goes in the
    /// strip beside the dates rather than anywhere new: a promotion post is answering when-and-where
    /// as one question, and an audience outside Denmark cannot infer the country from the wordmark
    /// alone — "Experts Live Denmark" is a brand, not an address. Value comes from the event config
    /// (<c>Bella Center Copenhagen</c> / <c>Copenhagen, Denmark</c>), not from the renderer.
    /// </remarks>
    public string? EventLocation { get; set; }

    private float RingCx => BadgeOnLeft ? Width - RingCentreX : RingCentreX;

    /// <summary>
    /// The column the name, session title and event strip all share — whatever the badge leaves.
    /// </summary>
    private float TextColumnWidth => BadgeOnLeft
        ? Width - (RingCx + RingOuterRadius) - 110f
        : RingCx - RingOuterRadius - 110f;

    private float TextColumnX => BadgeOnLeft ? RingCx + RingOuterRadius + 55f : EdgeMargin;

    /// <summary>
    /// The ONE margin both edges use — measured from the badge, which is the element that cannot move.
    /// </summary>
    /// <remarks>
    /// 🔒 Operator 2026-08-01: <i>"left and right must have a balance"</i>. The badge's outer edge
    /// sits <c>Width - (887 + 249) = 64 px</c> from the right, while the text column started at 70 —
    /// near enough to look like an accident and far enough to see. Derived rather than typed, so the
    /// two edges cannot drift apart again if the ring geometry is ever retuned.
    /// </remarks>
    private const float EdgeMargin = Width - RingCentreX - RingOuterRadius;

    /// <summary>Height of the plate carrying the SPEAKER word.</summary>
    private const float BadgePlateHeight = RingBandWidth * 1.30f;

    /// <summary>Top of that plate — kept here so the strip can line up with it.</summary>
    private const float BadgePlateTop = RingCentreY + RingOuterRadius - (BadgePlateHeight * 0.95f);

    /// <summary>
    /// The line the event strip sits on: the SPEAKER word's own centre line.
    /// </summary>
    /// <remarks>
    /// 🔒 Operator 2026-08-01: <i>"align date so it aligns with bottom of box or text in right
    /// side"</i>. The strip used to be pinned to the canvas bottom, which put it ~20 px below the
    /// badge word — reading as two separate afterthoughts rather than one baseline. Tying it to the
    /// PLATE means the two sides stay level even if the ring moves.
    /// </remarks>
    private const float EventStripY = BadgePlateTop + (BadgePlateHeight / 2f);

    // ---- the locked design --------------------------------------------------------------

    /// <summary>
    /// A renderer configured to the design the operator APPROVED — the one production must use.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>This is the lock.</b> Operator 2026-08-01: <i>"amazing a3, c2"</i> (layout),
    /// <i>"gif works flawless just need the design to be locked down"</i>, <i>"yes do the horizontal
    /// crop"</i>. Every value below is a decision he made, not a default somebody chose:</para>
    /// <list type="bullet">
    /// <item>two-tone ring, badge right, session title under the name (his A3)</item>
    /// <item>white event mark, from the Template folder when a file is supplied</item>
    /// <item>sponsor panel with the mark centred above it and a tier caption (his C2)</item>
    /// <item>the bottom strip carries <b>dates and location only</b> — the wordmark already says the
    /// name, and adding it back is what made the layout cramped</item>
    /// <item><b>CropZoom 0.56, anchored left</b> — the only value that fully clears last year's stage
    /// screens. 0.80 and 0.68 both still showed ELDK26 branding; that is why this number and not a
    /// gentler one.</item>
    /// </list>
    /// <para>⚠️ Changing anything here changes every graphic ELDK publishes. It is a design decision,
    /// not a tuning knob — the variants stay available as properties for the next time he compares.</para>
    /// </remarks>
    public static SoMeGraphicRenderer LockedDesign() => new()
    {
        TwoToneRing = true,
        WhiteEventLogo = true,
        SponsorEventLogo = true,
        CropZoom = 0.56f,
        CropAnchorX = 0f,
    };

    // ---- public API --------------------------------------------------------------------

    /// <summary>One speaker: template + circular photo badge + name. Returns PNG bytes.</summary>
    public byte[] RenderSpeakerPng(byte[] template, byte[]? photo, string speakerName)
    {
        using var canvas = BuildBackground(template);
        DrawSpeakerLayer(canvas, photo, speakerName);
        return ToPng(canvas);
    }

    /// <summary>
    /// Several speakers: ONE FRAME EACH, in the identical layout — which is exactly what the ELDK26
    /// GIFs are. A GIF here is not a different design, it is the PNG repeated per person.
    /// </summary>
    public byte[] RenderSpeakerGif(
        byte[] template, IReadOnlyList<(byte[]? Photo, string Name)> speakers,
        int frameDelayCentiseconds = 200)
    {
        if (speakers.Count == 0) throw new ArgumentException("A GIF needs at least one speaker.", nameof(speakers));

        return BuildGif(
            template, speakers.Count, frameDelayCentiseconds,
            (frame, i) => DrawSpeakerLayer(frame, speakers[i].Photo, speakers[i].Name));
    }

    /// <summary>
    /// Several sponsors in ONE GIF — a category or type bundle, one frame per sponsor.
    /// </summary>
    /// <remarks>
    /// 🔒 §767, operator 2026-08-01: <i>"make folder for track speakers and sponsor category sponsor
    /// (gif bundles)"</i>. Same rule as the speaker GIF: a bundle is not a different design, it is
    /// the single-sponsor layout repeated per member. <paramref name="caption"/> is the grouping's
    /// own label ("Platinum sponsor", "Beverage partner") and is identical on every frame, so the
    /// bundle reads as one statement rather than a slideshow of unrelated cards.
    /// </remarks>
    public byte[] RenderSponsorGif(
        byte[] template, IReadOnlyList<byte[]> logos, string? caption = null,
        int frameDelayCentiseconds = 200)
    {
        if (logos.Count == 0) throw new ArgumentException("A GIF needs at least one sponsor.", nameof(logos));

        var previousCaption = SponsorCaption;
        if (!string.IsNullOrWhiteSpace(caption)) SponsorCaption = caption;
        try
        {
            return BuildGif(
                template, logos.Count, frameDelayCentiseconds,
                (frame, i) => DrawSponsorLayer(frame, logos[i]));
        }
        finally { SponsorCaption = previousCaption; }
    }

    /// <summary>
    /// The shared GIF assembly: one background, cloned per frame, each frame drawn by the caller.
    /// </summary>
    /// <remarks>
    /// 🔑 The background is composed ONCE and cloned: the crop, resize and tone-down are the
    /// expensive part and are identical on every frame. An 11-speaker track GIF would otherwise redo
    /// that work eleven times for no visible difference.
    /// </remarks>
    private byte[] BuildGif(
        byte[] template, int frameCount, int frameDelayCentiseconds, Action<Image<Rgba32>, int> drawFrame)
    {
        using var background = BuildBackground(template);

        Image<Rgba32>? gif = null;
        try
        {
            for (var i = 0; i < frameCount; i++)
            {
                var frame = background.Clone();
                drawFrame(frame, i);

                if (gif is null)
                {
                    gif = frame;
                    var root = gif.Frames.RootFrame.Metadata.GetGifMetadata();
                    root.FrameDelay = frameDelayCentiseconds;
                }
                else
                {
                    using (frame)
                    {
                        var added = gif.Frames.AddFrame(frame.Frames.RootFrame);
                        added.Metadata.GetGifMetadata().FrameDelay = frameDelayCentiseconds;
                    }
                }
            }

            // RepeatCount 0 = loop forever, matching the ELDK26 files.
            gif!.Metadata.GetGifMetadata().RepeatCount = 0;

            using var ms = new MemoryStream();
            gif.SaveAsGif(ms, new GifEncoder { ColorTableMode = GifColorTableMode.Local });
            return ms.ToArray();
        }
        finally { gif?.Dispose(); }
    }

    /// <summary>One sponsor: template + the white panel with the logo fitted inside.</summary>
    public byte[] RenderSponsorPng(byte[] template, byte[] logo)
    {
        using var canvas = BuildBackground(template);
        DrawSponsorLayer(canvas, logo);
        return ToPng(canvas);
    }

    // ---- layers ------------------------------------------------------------------------

    /// <summary>Template → cropped to the output ratio → toned down → white frame.</summary>
    private Image<Rgba32> BuildBackground(byte[] template)
    {
        using var source = Image.Load<Rgba32>(template);

        // Crop to the output aspect BEFORE resizing, anchored per CropAnchor, so nothing is squashed.
        var targetRatio = (float)Width / Height;
        var cropWidth = (int)(source.Width * Math.Clamp(CropZoom, 0.3f, 1f));
        var cropHeight = (int)(cropWidth / targetRatio);
        if (cropHeight > source.Height)
        {
            cropHeight = source.Height;
            cropWidth = (int)(source.Height * targetRatio);
        }
        // Both anchors now do the same job on their own axis: 0 = keep the left/top edge, 1 = the
        // right/bottom. When there is no slack on an axis the anchor is simply a no-op.
        var left = (int)((source.Width - cropWidth) * Math.Clamp(CropAnchorX, 0f, 1f));
        var top = (int)((source.Height - cropHeight) * Math.Clamp(CropAnchor, 0f, 1f));

        var canvas = source.Clone(c => c
            .Crop(new Rectangle(left, top, cropWidth, cropHeight))
            .Resize(Width, Height));

        // Tone-down layer. The template arrives full-brightness and busy; white text and a white
        // frame need it dark to read at thumbnail size in a LinkedIn feed.
        if (ToneDown > 0f)
        {
            var shade = Color.Black.WithAlpha(Math.Clamp(ToneDown, 0f, 1f));
            canvas.Mutate(c => c.Fill(shade, new RectangleF(0, 0, Width, Height)));
        }

        // Frame layer: the thin white inset border.
        canvas.Mutate(c => c.Draw(Color.White, FrameThickness, new RectangleF(
            FrameInset, FrameInset, Width - (2 * FrameInset), Height - (2 * FrameInset))));

        return canvas;
    }

    private void DrawSpeakerLayer(Image<Rgba32> canvas, byte[]? photo, string speakerName)
    {
        DrawEventLogo(canvas);
        DrawRingBadge(canvas, photo);
        DrawSpeakerName(canvas, speakerName);
        DrawEventStrip(canvas, centred: false);
    }

    /// <summary>
    /// The event name and dates along the bottom — "EXPERTS LIVE DENMARK 2027 · 9-10 FEB 2027".
    /// </summary>
    /// <remarks>
    /// Either value alone is enough to draw the strip: an edition with no dates published yet still
    /// gets its name on the graphic rather than silently losing the line.
    /// </remarks>
    private void DrawEventStrip(Image<Rgba32> canvas, bool centred)
    {
        var parts = new[] { EventName, EventDates, EventLocation }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim().ToUpperInvariant())
            .ToArray();
        if (parts.Length == 0) return;

        var text = string.Join("  ·  ", parts);
        var font = Font(25f, FontStyle.Bold);
        var options = new RichTextOptions(font)
        {
            HorizontalAlignment = centred ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            // Left-aligned it follows the name column, so mirroring the badge moves it too; and it
            // sits on the badge word's line, not the canvas bottom, so the two sides read as one row.
            Origin = new PointF(centred ? Width / 2f : TextColumnX, EventStripY),
        };

        // 🔒 Clipped to the TEXT COLUMN, never the full canvas. Operator 2026-08-01: "i am worried of
        // too much text makes it cramped and noisy" — and he was right: unclipped, name + dates +
        // location ran straight under the SPEAKER plate. Wrapping instead of overlapping means a
        // strip that is too long becomes visibly too long, which is a decision he can see and make,
        // rather than a collision.
        if (!centred) options.WrappingLength = TextColumnWidth;

        canvas.Mutate(c => c.DrawText(options, text, Color.White));
    }

    /// <summary>
    /// The Experts Live Denmark wordmark, top-left — the same embedded asset the evaluation report
    /// uses (§750.8), so the two surfaces cannot drift onto different logos.
    /// </summary>
    /// <remarks>
    /// Absent logo ⇒ skipped, not thrown: a graphic without the mark is still usable, and this is a
    /// nicety rather than the payload (the same trade the report makes).
    /// </remarks>
    private void DrawEventLogo(Image<Rgba32> canvas, bool centred = false)
    {
        // The file from the Template folder wins; the embedded colour mark is only the fallback.
        var supplied = EventLogo is { Length: > 0 };
        var bytes = supplied ? EventLogo : CommunityHub.Core.Evaluation.EmbeddedFontResolver.LogoBytes();
        if (bytes is null || bytes.Length == 0) return;

        using var logo = Image.Load<Rgba32>(bytes);
        const int targetWidth = 330;
        var scale = targetWidth / (float)logo.Width;
        logo.Mutate(c => c.Resize(targetWidth, Math.Max(1, (int)(logo.Height * scale))));
        // A supplied mark is used exactly as delivered — he owns that file, so nothing recolours it.
        if (!supplied && WhiteEventLogo) KnockOutToWhite(logo);
        // Speakers: top-left, because the badge owns the right half. Sponsors: centred top, because
        // the panel is symmetric and the ELDK26 sponsor layout centres it.
        var x = centred ? (Width - targetWidth) / 2 : 62;
        canvas.Mutate(c => c.DrawImage(logo, new Point(x, 46), 1f));
    }

    /// <summary>
    /// Repaint every pixel white, keeping its alpha — the mark's SHAPE survives, its colours do not.
    /// </summary>
    /// <remarks>
    /// ⚠️ Alpha is preserved, not thresholded, so the anti-aliased edges stay smooth. Thresholding
    /// would give the wordmark a jagged outline at this size.
    /// </remarks>
    private static void KnockOutToWhite(Image<Rgba32> logo)
    {
        logo.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgba32(255, 255, 255, row[x].A);
                }
            }
        });
    }

    /// <summary>
    /// The circular badge: photo cropped to a circle, a blue band around it carrying the curved
    /// caption, and the SPEAKER word across the bottom.
    /// </summary>
    private void DrawRingBadge(Image<Rgba32> canvas, byte[]? photo)
    {
        var centre = new PointF(RingCx, RingCentreY);
        var photoRadius = RingOuterRadius - RingBandWidth - PhotoGap;

        // Outer white disc — reads as the ring's edge and separates it from a busy background.
        canvas.Mutate(c => c.Fill(Color.White, new SixLabors.ImageSharp.Drawing.EllipsePolygon(centre, RingOuterRadius)));
        // The blue band. TwoToneRing adds the darker outer rim the ELDK26 badge has, which gives the
        // circle an edge against a light background instead of bleeding into it.
        if (TwoToneRing)
        {
            canvas.Mutate(c => c.Fill(BrandDarkBlue, new SixLabors.ImageSharp.Drawing.EllipsePolygon(centre, RingOuterRadius - 6f)));
            canvas.Mutate(c => c.Fill(BrandBlue, new SixLabors.ImageSharp.Drawing.EllipsePolygon(centre, RingOuterRadius - 16f)));
        }
        else
        {
            canvas.Mutate(c => c.Fill(BrandBlue, new SixLabors.ImageSharp.Drawing.EllipsePolygon(centre, RingOuterRadius - 6f)));
        }
        // White gap, then the photo well.
        canvas.Mutate(c => c.Fill(Color.White, new SixLabors.ImageSharp.Drawing.EllipsePolygon(centre, photoRadius + PhotoGap)));

        if (photo is { Length: > 0 })
        {
            using var face = Image.Load<Rgba32>(photo);
            var side = (int)(photoRadius * 2);
            face.Mutate(c => c.Resize(new ResizeOptions { Mode = ResizeMode.Crop, Size = new Size(side, side) }));
            // 🔑 Circular mask: ImageSharp has no "clip to shape" for DrawImage, so the corners are
            // cleared by drawing the face and then filling the area OUTSIDE the circle back to the
            // white well — done on a scratch layer so the background is untouched.
            using var masked = new Image<Rgba32>(side, side);
            masked.Mutate(c => c.DrawImage(face, new Point(0, 0), 1f));
            ApplyCircleMask(masked);
            canvas.Mutate(c => c.DrawImage(
                masked, new Point((int)(centre.X - photoRadius), (int)(centre.Y - photoRadius)), 1f));
        }

        DrawCurvedCaption(canvas, centre);
        DrawBadgeWord(canvas, centre);
    }

    /// <summary>Clear every pixel outside the inscribed circle, so the face reads as round.</summary>
    private static void ApplyCircleMask(Image<Rgba32> square)
    {
        var r = square.Width / 2f;
        var cx = r;
        var cy = r;
        square.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var dx = x - cx + 0.5f;
                    var dy = y - cy + 0.5f;
                    if ((dx * dx) + (dy * dy) > r * r) row[x] = default; // transparent
                }
            }
        });
    }

    /// <summary>
    /// The caption around the top of the ring. ⚠️ ImageSharp has NO text-on-a-path primitive, so
    /// each character is drawn individually with a rotation about the ring centre.
    /// </summary>
    private void DrawCurvedCaption(Image<Rgba32> canvas, PointF centre)
    {
        var font = Font(22f, FontStyle.Bold);
        var textRadius = RingOuterRadius - (RingBandWidth / 2f) - 4f;

        // Spread the caption over the top ~240° of the circle, centred on 12 o'clock.
        const float arc = 4.2f;                       // radians of sweep
        var chars = RingCaption.ToCharArray();
        var step = arc / Math.Max(1, chars.Length - 1);
        var start = -arc / 2f;

        for (var i = 0; i < chars.Length; i++)
        {
            var angle = start + (i * step);
            var options = new RichTextOptions(font)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Origin = new PointF(centre.X, centre.Y - textRadius),
            };

            // Rotate the whole drawing plane about the ring centre, then draw the glyph at 12
            // o'clock — the transform carries it round to its own angle, upright to the circle.
            var transform = Matrix3x2.CreateRotation(angle, new Vector2(centre.X, centre.Y));
            canvas.Mutate(c => c
                .SetDrawingTransform(transform)
                .DrawText(options, chars[i].ToString(), Color.White)
                .SetDrawingTransform(Matrix3x2.Identity));
        }
    }

    /// <summary>The SPEAKER banner across the bottom of the ring.</summary>
    private void DrawBadgeWord(Image<Rgba32> canvas, PointF centre)
    {
        var bandHeight = RingBandWidth * 1.30f;
        var bandY = centre.Y + RingOuterRadius - (bandHeight * 0.95f);
        // 🔑 Inset so the plate stays INSIDE the circle's width — the first render let it
        // overhang the ring on both sides, which read as a bar stuck onto the badge rather than
        // part of it. A chord across a circle at this height is ~1.45r wide.
        var bandWidth = RingOuterRadius * 1.42f;

        // A darker plate so the word reads against the lighter band behind it, with rounded ends
        // that follow the badge rather than cutting square across it.
        canvas.Mutate(c => c.Fill(BrandDarkBlue, RoundedRect(
            new RectangleF(centre.X - (bandWidth / 2f), bandY, bandWidth, bandHeight),
            bandHeight / 2.6f)));

        var font = Font(40f, FontStyle.Bold);
        var options = new RichTextOptions(font)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Origin = new PointF(centre.X, bandY + (bandHeight / 2f)),
        };
        canvas.Mutate(c => c.DrawText(options, BadgeLabel, Color.White));
    }

    /// <summary>The speaker's name, lower-left, uppercase, wrapping to a second line.</summary>
    private void DrawSpeakerName(Image<Rgba32> canvas, string speakerName)
    {
        if (string.IsNullOrWhiteSpace(speakerName)) return;

        // The text column is whatever the badge does NOT occupy, so mirroring the badge moves the
        // name with it rather than letting the two overlap.
        // Shared with the event strip, so the name, the title and the dates all start on one line
        // and the left margin equals the badge's gap on the right.
        var columnWidth = TextColumnWidth;
        var originX = TextColumnX;

        var hasSubtitle = !string.IsNullOrWhiteSpace(Subtitle);
        var nameY = hasSubtitle ? 292f : 330f;

        var font = Font(58f, FontStyle.Bold);
        var options = new RichTextOptions(font)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Origin = new PointF(originX, nameY),
            WrappingLength = columnWidth,
            LineSpacing = 1.05f,
        };
        canvas.Mutate(c => c.DrawText(options, speakerName.ToUpperInvariant(), Color.White));

        if (hasSubtitle)
        {
            // Session title: smaller, regular weight, clamped to three lines' worth of space so a
            // long title cannot run off the canvas or collide with the badge.
            var subFont = Font(27f, FontStyle.Regular);
            var subOptions = new RichTextOptions(subFont)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Origin = new PointF(originX, nameY + 78f),
                WrappingLength = columnWidth,
                LineSpacing = 1.15f,
            };
            canvas.Mutate(c => c.DrawText(subOptions, Subtitle!, Color.White));
        }
    }

    private void DrawSponsorLayer(Image<Rgba32> canvas, byte[] logo)
    {
        if (SponsorEventLogo) DrawEventLogo(canvas, centred: true);

        // ---- the panel is sized TO THE LOGO, not to the canvas -----------------------------
        // 🔒 Operator 2026-08-01: "dimension for sponsor logo in white box must be adjusted. width is
        // to big compared to logo dimensions". A fixed full-width plate left a square or short
        // wordmark stranded in a sea of white. So: fit the mark first, then build the panel around
        // it with even padding — the box now follows the logo's own proportions.
        using var mark = Image.Load<Rgba32>(logo);
        // 🔒 THE LOGO'S HEIGHT DRIVES EVERYTHING. Operator 2026-08-01: "can the height of sponsor
        // logo with smaller gap decide width of white box". So the mark is scaled to fill the panel
        // HEIGHT first, against a tight vertical gap, and the panel's WIDTH then simply follows the
        // logo's own aspect ratio. A tall square mark gets a near-square box, a long wordmark gets a
        // long one — the plate takes the shape of what it carries instead of imposing one.
        const float padY = 28f;
        const float padX = 52f;
        var targetH = (int)(SponsorPanelMaxHeight - (2 * padY));
        // The width cap only stops an extremely long wordmark overflowing the canvas; for every
        // normal mark the HEIGHT is what binds, which is the point.
        var maxW = (int)(Width - (2 * SponsorPanelMargin) - (2 * padX));
        mark.Mutate(c => c.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(maxW, targetH) }));

        // A minimum so a very thin mark still lands on a plate with presence rather than a sliver.
        var panelW = Math.Clamp(mark.Width + (2 * padX), 360f, Width - (2 * SponsorPanelMargin));
        var panelH = mark.Height + (2 * padY);
        // Kept centred on the band the full-width plate occupied, so the panel grows and shrinks
        // about its middle rather than creeping up the canvas.
        var panelY = SponsorPanelTop + ((SponsorPanelMaxHeight - panelH) / 2f);
        var panel = new RectangleF((Width - panelW) / 2f, panelY, panelW, panelH);

        canvas.Mutate(c => c.Fill(Color.White, RoundedRect(panel, 28f)));
        canvas.Mutate(c => c.Draw(BrandBlue, 3f, RoundedRect(panel, 28f)));

        if (!string.IsNullOrWhiteSpace(SponsorCaption))
        {
            var capFont = Font(30f, FontStyle.Bold);
            var capOptions = new RichTextOptions(capFont)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Origin = new PointF(Width / 2f, panel.Y - 42f),
            };
            canvas.Mutate(c => c.DrawText(capOptions, SponsorCaption!.ToUpperInvariant(), Color.White));
        }

        var x = (int)(panel.X + ((panel.Width - mark.Width) / 2f));
        var y = (int)(panel.Y + ((panel.Height - mark.Height) / 2f));
        canvas.Mutate(c => c.DrawImage(mark, new Point(x, y), 1f));

        DrawEventStrip(canvas, centred: true);
    }

    /// <summary>A rounded rectangle path — the sponsor panel's shape.</summary>
    /// <remarks>
    /// ⚠️ Built from FOUR ARCS ONLY, letting <c>PathBuilder</c> join them with straight edges.
    /// Interleaving explicit <c>AddLine</c> calls between the arcs produced a malformed figure with
    /// spikes at the corners — visible in the first render as notches poking out of the panel.
    /// </remarks>
    private static SixLabors.ImageSharp.Drawing.IPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2f;
        var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
        pb.StartFigure();
        pb.AddArc(new RectangleF(r.Left, r.Top, d, d), 0, 180, 90);                  // top-left
        pb.AddArc(new RectangleF(r.Right - d, r.Top, d, d), 0, 270, 90);             // top-right
        pb.AddArc(new RectangleF(r.Right - d, r.Bottom - d, d, d), 0, 0, 90);        // bottom-right
        pb.AddArc(new RectangleF(r.Left, r.Bottom - d, d, d), 0, 90, 90);            // bottom-left
        pb.CloseFigure();
        return pb.Build();
    }

    // ---- fonts + output ----------------------------------------------------------------

    private static readonly FontCollection Fonts = LoadEmbeddedFonts();
    private static readonly FontFamily Family = Fonts.Families.First();

    /// <summary>
    /// 🔒 The EMBEDDED Open Sans (§750.7). Never <c>SystemFonts</c>: Linux App Service has none, and
    /// a renderer that silently drops its text produces a graphic that looks finished and says
    /// nothing.
    /// </summary>
    private static FontCollection LoadEmbeddedFonts()
    {
        var collection = new FontCollection();
        var asm = typeof(SoMeGraphicRenderer).Assembly;
        foreach (var name in new[]
                 {
                     "CommunityHub.Core.Evaluation.Fonts.OpenSans-Bold.ttf",
                     "CommunityHub.Core.Evaluation.Fonts.OpenSans-Regular.ttf",
                 })
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s is not null) collection.Add(s);
        }
        if (collection.Families.Any()) return collection;
        throw new InvalidOperationException(
            "SoMeGraphicRenderer: the embedded Open Sans faces are missing from CommunityHub.Core. "
            + "The graphics cannot be rendered without them — see §750.7 (no fonts on Linux hosts).");
    }

    private static Font Font(float size, FontStyle style) => Family.CreateFont(size, style);

    private static byte[] ToPng(Image image)
    {
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }
}
