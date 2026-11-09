using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>§1384 — one track of the all-tracks overview: its name and every speaker in it.</summary>
public sealed record TrackOverviewTrack(string TrackName, IReadOnlyList<(byte[]? Photo, string Name)> Speakers);

/// <summary>
/// §1384 — where the faces of ONE overview frame go. Pure numbers, so the "nothing overlaps the
/// footer" rule is a test, not a hope.
/// </summary>
public sealed record TrackOverviewLayout(
    int Rows, int Cols, float Diameter, float PitchX, float RowHeight, float NameFontSize,
    float LineHeight, float GridTop, float GridBottom, IReadOnlyList<PointF> Centres);

/// <summary>
/// §1384 — the ALL TRACKS · ALL SPEAKERS overview GIF: one frame per track, every speaker in that
/// track as a circular face with their name underneath.
/// </summary>
/// <remarks>
/// <para>Operator 2026-11-07: <i>"one pic for security with all speakers in a frame, then move to
/// intune, etc"</i>. A NEW output beside the locked §767 designs — none of the existing methods,
/// constants or the locked design are touched. It shares the embedded Open Sans faces
/// (<see cref="Font"/>), the circle mask and the GIF encoder settings of the speaker GIF.</para>
///
/// <para>🔑 <b>No template photograph.</b> The frame is a plain navy gradient: the overview is read
/// at mail width (600 px), where a toned-down photo behind 12–24 small faces is noise.</para>
///
/// <para>🔒 <b>Frame 1 must stand alone.</b> Outlook desktop (and new Outlook with GIF autoplay off)
/// shows only the first frame, so every frame carries the event name, the dates and the track
/// count — never a "title card" that says nothing on its own.</para>
/// </remarks>
public sealed partial class SoMeGraphicRenderer
{
    /// <summary>The top of the face grid — below the subtitle line.</summary>
    public const float OverviewGridTop = 175f;

    /// <summary>The top of the footer band. Nothing in the grid may reach below this.</summary>
    public const float OverviewFooterTop = Height - 60f;

    /// <summary>3.5 s a frame (§1384 spec).</summary>
    public const int OverviewFrameDelayCentiseconds = 350;

    /// <summary>The widths the public overview URL serves (1200 = native, 600 = mail).</summary>
    public static readonly IReadOnlyList<int> OverviewWidths = new[] { 1200, 600 };

    private static readonly Color OverviewNavyTop = Color.ParseHex("0B1E5B");
    private static readonly Color OverviewNavyBottom = Color.ParseHex("050F33");
    private static readonly Color OverviewAccent = Color.ParseHex("5AA9E6");
    private static readonly Color OverviewSubtitle = Color.ParseHex("9EC9F2");

    /// <summary>
    /// The speaker claim on the right of the footer, e.g. <c>90+ SPEAKERS</c>. A marketing number
    /// the operator chooses, not a count — the line-up grows every week.
    /// </summary>
    public string OverviewSpeakerClaim { get; set; } = "90+ SPEAKERS";

    /// <summary>
    /// §1384 — the overview GIF: one frame per track, in the order given. Every frame is complete on
    /// its own (Outlook shows only frame 1).
    /// </summary>
    public byte[] RenderTrackOverviewGif(
        IReadOnlyList<TrackOverviewTrack> tracks,
        int frameDelayCentiseconds = OverviewFrameDelayCentiseconds)
    {
        var drawable = tracks.Where(t => t.Speakers.Count > 0).ToList();
        if (drawable.Count == 0)
            throw new ArgumentException("The overview needs at least one track with a speaker.", nameof(tracks));

        Image<Rgba32>? gif = null;
        try
        {
            foreach (var track in drawable)
            {
                var frame = new Image<Rgba32>(Width, Height);
                DrawOverviewFrame(frame, track, drawable.Count);

                if (gif is null)
                {
                    gif = frame;
                    gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = frameDelayCentiseconds;
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

            // Loop forever, like every other GIF this renderer writes.
            gif!.Metadata.GetGifMetadata().RepeatCount = 0;

            using var ms = new MemoryStream();
            gif.SaveAsGif(ms, new GifEncoder { ColorTableMode = GifColorTableMode.Local });
            return ms.ToArray();
        }
        finally { gif?.Dispose(); }
    }

    /// <summary>
    /// §1384 — a stored overview GIF scaled to <paramref name="width"/> (aspect kept, every frame
    /// and its delay preserved). The native width is returned as-is.
    /// </summary>
    public static byte[] ResizeGif(byte[] gif, int width)
    {
        using var image = Image.Load<Rgba32>(gif);
        if (image.Width == width) return gif;
        image.Mutate(c => c.Resize(width, 0));
        using var ms = new MemoryStream();
        image.SaveAsGif(ms, new GifEncoder { ColorTableMode = GifColorTableMode.Local });
        return ms.ToArray();
    }

    /// <summary>
    /// The track title as printed: a trailing parenthetical is dropped —
    /// <c>AI for Makers (Copilot &amp; Agents)</c> → <c>AI for Makers</c>.
    /// </summary>
    public static string OverviewTitle(string trackName)
    {
        var t = (trackName ?? string.Empty).Trim();
        if (t.EndsWith(')'))
        {
            var open = t.LastIndexOf(" (", StringComparison.Ordinal);
            if (open > 0) t = t[..open].TrimEnd();
        }
        return t;
    }

    /// <summary>Speakers WITH a photo first, then alphabetical — the face grid reads full from the top.</summary>
    public static IReadOnlyList<(byte[]? Photo, string Name)> OverviewOrder(
        IEnumerable<(byte[]? Photo, string Name)> speakers) =>
        speakers
            .OrderBy(s => s.Photo is { Length: > 0 } ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The grid for <paramref name="count"/> speakers: ≤6 one row, ≤20 two rows, else three
    /// (cols = ⌈n / rows⌉). Cell width = min(170, 1120 / cols); face = min(118, cell − 20) on one or
    /// two rows, min(76, cell − 20) on three; name font 15 px for a cell ≥ 140, 13 px ≥ 110, else
    /// 12 px. Rows are centred horizontally and the grid vertically between
    /// <see cref="OverviewGridTop"/> and <see cref="OverviewFooterTop"/>; faces shrink before
    /// anything reaches the footer.
    /// </summary>
    /// <remarks>
    /// §1384 follow-up (PROD check 2026-11-07): three rows of 76 px faces from 13 speakers up left
    /// Security (13) and Intune (19) small with empty space around them — two rows hold up to 20.
    /// </remarks>
    public static TrackOverviewLayout OverviewLayout(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

        var rows = count <= 6 ? 1 : count <= 20 ? 2 : 3;
        var cols = (int)Math.Ceiling(count / (double)rows);
        var pitchX = Math.Min(170f, OverviewGridWidth / cols);
        var diameter = Math.Min(rows <= 2 ? 118f : 76f, pitchX - 20f);
        var nameFont = pitchX >= 140f ? 15f : pitchX >= 110f ? 13f : 12f;
        var rowGap = rows <= 2 ? 22f : 10f;
        var available = OverviewFooterTop - OverviewGridTop;

        float lineHeight, rowHeight, gridHeight;
        while (true)
        {
            lineHeight = nameFont * 1.25f;
            // ring (3) + gap (6) under the face, then two name lines.
            rowHeight = diameter + 9f + (2 * lineHeight);
            gridHeight = (rows * rowHeight) + ((rows - 1) * rowGap);
            if (gridHeight <= available || diameter <= 30f) break;
            diameter -= 2f;
            nameFont = Math.Max(10f, nameFont - 0.25f);
        }

        var top = OverviewGridTop + Math.Max(0f, (available - gridHeight) / 2f);
        var centres = new List<PointF>(count);
        for (var r = 0; r < rows; r++)
        {
            var inRow = r < rows - 1 ? cols : count - (cols * (rows - 1));
            if (inRow <= 0) continue;
            var rowWidth = inRow * pitchX;
            var x0 = ((Width - rowWidth) / 2f) + (pitchX / 2f);
            var cy = top + (r * (rowHeight + rowGap)) + (diameter / 2f);
            for (var c = 0; c < inRow; c++) centres.Add(new PointF(x0 + (c * pitchX), cy));
        }

        return new TrackOverviewLayout(
            rows, cols, diameter, pitchX, rowHeight, nameFont, lineHeight,
            top, top + gridHeight, centres);
    }

    /// <summary>The width the face grid may use (1200 − 2 × 40).</summary>
    public const float OverviewGridWidth = 1120f;

    /// <summary>The smallest a name is ever printed.</summary>
    public const float OverviewMinNameFont = 9f;

    /// <summary>The space a name line may take in its cell, so it never touches the neighbour's.</summary>
    public static float OverviewNameWidth(TrackOverviewLayout layout) => layout.PitchX - 8f;

    /// <summary>Rendered width of <paramref name="text"/> in the embedded bold face.</summary>
    public static float MeasureBold(string text, float size) =>
        TextMeasurer.MeasureSize(text, new TextOptions(Font(size, FontStyle.Bold))).Width;

    /// <summary>
    /// The font size a line is printed at: <paramref name="size"/>, reduced 0.5 px at a time until it
    /// fits <paramref name="maxWidth"/> (never below <see cref="OverviewMinNameFont"/>).
    /// </summary>
    public static float FitFontSize(string text, float size, float maxWidth, FontStyle style = FontStyle.Bold)
    {
        while (size > OverviewMinNameFont
               && TextMeasurer.MeasureSize(text, new TextOptions(Font(size, style))).Width > maxWidth)
            size -= 0.5f;
        return Math.Max(size, OverviewMinNameFont);
    }

    /// <summary>"Ada" / "King Lovelace" — first name over the rest, as the grid prints it.</summary>
    public static (string First, string Remainder) OverviewNameLines(string name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return (string.Empty, string.Empty);
        return (parts[0], string.Join(' ', parts.Skip(1)));
    }

    // ---- drawing ------------------------------------------------------------------------

    private void DrawOverviewFrame(Image<Rgba32> canvas, TrackOverviewTrack track, int trackCount)
    {
        canvas.Mutate(c => c.Fill(new LinearGradientBrush(
            new PointF(0, 0), new PointF(0, Height), GradientRepetitionMode.None,
            new ColorStop(0f, OverviewNavyTop), new ColorStop(1f, OverviewNavyBottom))));

        // Header: event name top-left, the track title centred, the speaker count under it.
        if (!string.IsNullOrWhiteSpace(EventName))
        {
            canvas.Mutate(c => c.DrawText(new RichTextOptions(Font(26f, FontStyle.Bold))
            {
                Origin = new PointF(40f, 42f),
                VerticalAlignment = VerticalAlignment.Center,
            }, EventName!.Trim(), Color.White));
        }

        DrawCentredFitted(canvas, OverviewTitle(track.TrackName), 46f, FontStyle.Bold, 110f, Width - 80f, Color.White);

        var n = track.Speakers.Count;
        DrawCentredFitted(canvas, $"{n} {(n == 1 ? "SPEAKER" : "SPEAKERS")} IN THE TRACK",
            20f, FontStyle.Regular, 150f, Width - 80f, OverviewSubtitle);

        // The grid.
        var speakers = OverviewOrder(track.Speakers);
        var layout = OverviewLayout(speakers.Count);
        for (var i = 0; i < speakers.Count; i++)
            DrawOverviewFace(canvas, layout, layout.Centres[i], speakers[i].Photo, speakers[i].Name);

        DrawOverviewFooter(canvas, trackCount);
    }

    private static void DrawOverviewFace(
        Image<Rgba32> canvas, TrackOverviewLayout layout, PointF centre, byte[]? photo, string name)
    {
        var radius = layout.Diameter / 2f;
        canvas.Mutate(c => c.Fill(OverviewAccent, new EllipsePolygon(centre, radius + 3f)));

        var drewPhoto = false;
        if (photo is { Length: > 0 })
        {
            try
            {
                using var face = Image.Load<Rgba32>(photo);
                var side = Math.Max(1, (int)layout.Diameter);
                face.Mutate(c => c.Resize(new ResizeOptions { Mode = ResizeMode.Crop, Size = new Size(side, side) }));
                using var masked = new Image<Rgba32>(side, side);
                masked.Mutate(c => c.DrawImage(face, new Point(0, 0), 1f));
                ApplyCircleMask(masked);
                canvas.Mutate(c => c.DrawImage(
                    masked, new Point((int)(centre.X - (side / 2f)), (int)(centre.Y - (side / 2f))), 1f));
                drewPhoto = true;
            }
            catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException)
            {
                // An unreadable photo degrades to initials rather than losing the frame.
            }
        }

        if (!drewPhoto)
        {
            canvas.Mutate(c => c.Fill(BrandDarkBlue, new EllipsePolygon(centre, radius)));
            canvas.Mutate(c => c.DrawText(new RichTextOptions(Font(radius * 0.8f, FontStyle.Bold))
            {
                Origin = centre,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }, Initials(name), Color.White));
        }

        var (first, rest) = OverviewNameLines(name);
        var maxWidth = OverviewNameWidth(layout);
        var y1 = centre.Y + radius + 9f + (layout.LineHeight / 2f);
        DrawNameLine(canvas, first, layout.NameFontSize, y1, maxWidth, centre.X);
        if (rest.Length > 0)
            DrawNameLine(canvas, rest, layout.NameFontSize, y1 + layout.LineHeight, maxWidth, centre.X);
    }

    /// <summary>
    /// A name line as printed: the font shrinks to fit the cell; a line still too wide at the
    /// smallest size is cut with "…", so it can never run into the neighbouring name.
    /// </summary>
    public static (string Text, float Size) FitNameLine(string text, float size, float maxWidth)
    {
        var fitted = FitFontSize(text, size, maxWidth);
        if (MeasureBold(text, fitted) <= maxWidth) return (text, fitted);

        var cut = text;
        while (cut.Length > 1 && MeasureBold(cut + "…", fitted) > maxWidth) cut = cut[..^1];
        return (cut.TrimEnd() + "…", fitted);
    }

    private static void DrawNameLine(Image<Rgba32> canvas, string text, float size, float y, float maxWidth, float x)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var (line, fitted) = FitNameLine(text, size, maxWidth);
        canvas.Mutate(c => c.DrawText(new RichTextOptions(Font(fitted, FontStyle.Bold))
        {
            Origin = new PointF(x, y),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        }, line, Color.White));
    }

    private void DrawOverviewFooter(Image<Rgba32> canvas, int trackCount)
    {
        var y = Height - 30f;
        canvas.Mutate(c => c.DrawLine(OverviewAccent.WithAlpha(0.35f), 1f,
            new PointF(40f, OverviewFooterTop + 6f), new PointF(Width - 40f, OverviewFooterTop + 6f)));

        var left = string.Join("  ·  ", new[] { EventDates, EventLocation }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim().ToUpperInvariant()));
        if (left.Length > 0)
        {
            canvas.Mutate(c => c.DrawText(new RichTextOptions(Font(18f, FontStyle.Bold))
            {
                Origin = new PointF(40f, y),
                VerticalAlignment = VerticalAlignment.Center,
            }, left, Color.White));
        }

        var right = $"{trackCount} {(trackCount == 1 ? "TRACK" : "TRACKS")}"
                    + (string.IsNullOrWhiteSpace(OverviewSpeakerClaim) ? string.Empty : $"  ·  {OverviewSpeakerClaim.Trim()}");
        canvas.Mutate(c => c.DrawText(new RichTextOptions(Font(18f, FontStyle.Bold))
        {
            Origin = new PointF(Width - 40f, y),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        }, right, OverviewAccent));
    }

    /// <summary>Centred text, shrunk (never below 9 px) until it fits <paramref name="maxWidth"/>.</summary>
    private static void DrawCentredFitted(
        Image<Rgba32> canvas, string text, float size, FontStyle style, float y, float maxWidth,
        Color color, float? x = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var font = Font(size, style);
        while (size > 9f && TextMeasurer.MeasureSize(text, new TextOptions(font)).Width > maxWidth)
        {
            size -= 1f;
            font = Font(size, style);
        }
        canvas.Mutate(c => c.DrawText(new RichTextOptions(font)
        {
            Origin = new PointF(x ?? Width / 2f, y),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        }, text, color));
    }

    private static string Initials(string name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        var first = char.ToUpperInvariant(parts[0][0]);
        return parts.Length == 1 ? first.ToString() : $"{first}{char.ToUpperInvariant(parts[^1][0])}";
    }
}
