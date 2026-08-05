using PdfSharp.Drawing;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §750.8 — the report's visual vocabulary, in one place.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Operator 2026-08-01:</b> <i>"people brag on linkedin and cut paste result so it must
/// look fantastic in layout"</i>. That changes what this document is for. It is no longer only an
/// internal record — a speaker will screenshot it and post it, so the top of the page has to survive
/// being cropped and read at thumbnail size. Hence one big number, its band beside it, the event
/// wordmark, and no chrome competing with them.</para>
///
/// <para>🔒 The four rating colours are the DEVICE's own buttons — dark green, light green, yellow,
/// red. They are not a palette choice and must not be prettified: an attendee who pressed the yellow
/// button should find yellow in the chart.</para>
/// </remarks>
internal static class EvaluationReportLayout
{
    // Brand.
    public static readonly XColor BrandDark = XColor.FromArgb(0x1D, 0x33, 0x80);
    public static readonly XColor BrandBlue = XColor.FromArgb(0x00, 0x8B, 0xD2);
    public static readonly XColor Ink = XColor.FromArgb(0x16, 0x20, 0x2B);
    public static readonly XColor Muted = XColor.FromArgb(0x6A, 0x72, 0x80);
    public static readonly XColor Hairline = XColor.FromArgb(0xD8, 0xDE, 0xE5);
    public static readonly XColor Panel = XColor.FromArgb(0xF4, 0xF6, 0xF8);

    // The device's four buttons.
    public static readonly XColor Rating4 = XColor.FromArgb(0x1E, 0x7A, 0x3C);
    public static readonly XColor Rating3 = XColor.FromArgb(0x6D, 0xBE, 0x45);
    public static readonly XColor Rating2 = XColor.FromArgb(0xE4, 0xB4, 0x00);
    public static readonly XColor Rating1 = XColor.FromArgb(0xC8, 0x32, 0x2A);

    /// <summary>
    /// §750.8 — the score's own colour, on the SAME green → yellow → red ramp as the buttons
    /// (operator 2026-08-01: <i>"use color in score % with green,yellow,red etc based on
    /// satisfaction score%"</i>).
    /// </summary>
    /// <remarks>
    /// 🔑 Deliberately the four BUTTON colours, not a separate scale. A score of 75 is coloured the
    /// same light green as the "Happy" button that mostly produced it, so the headline and the
    /// distribution beneath it tell one story rather than two. An earlier version used the brand blue
    /// for 60–79, which looked tidy and said nothing.
    /// 🔒 Semantic colour, never the brand accent — the accent is for identity, this is for meaning.
    /// </remarks>
    public static XColor BandColour(double? score) => score switch
    {
        >= 80 => Rating4,     // dark green  — very strong
        >= 60 => Rating3,     // light green — strong
        >= 40 => Rating2,     // yellow      — mixed
        not null => Rating1,  // red         — weak
        _ => Muted,           // no score yet
    };


    /// <summary>
    /// The scoring model in plain language, for the person reading the PDF rather than the brief.
    /// </summary>
    /// <remarks>
    /// 🔑 Operator: <i>"explain model used in report in human language"</i>. Written as sentences a
    /// speaker can read once and understand — the point is that nobody should have to ask what 78
    /// means, least of all after it has been screenshotted away from any context.
    /// </remarks>
    public static readonly string[] HowItWorks =
    {
        // 🔑 §752.6 (operator: *"how this score works must include qr code in wording"*) — the old
        // opening said "everyone in the room pressed one of four buttons", which quietly wrote the QR
        // half of the audience out of the explanation. The header counts both channels, so a speaker
        // reading "QR code: 5 (20%)" above and "everyone in the room pressed" here is being told two
        // different stories. Both routes lead to the SAME four buttons and are weighted identically —
        // that is the reassuring part, and it only lands if the QR code is named.
        "Everyone chose one of four buttons — on the feedback device in the room, or by scanning the",
        "QR code on your slide. Both count exactly the same. Each button is worth points: very happy",
        "100, happy 67, somewhat unhappy 33, unhappy 0. The score is simply the average of those",
        "points, so 100 would mean every single person pressed the darkest green, and 0 that nobody did.",
        // §752.6 — "index" is gone. It was there to stop the number being read as a percentage, but a
        // word the reader has to look up cannot correct a misreading. Saying what the number is NOT,
        // in the words they already use, does the same job and needs no glossary.
        "It runs from 0 to 100. It is not a percentage: 83 does not mean 83% of people were happy.",
    };

    /// <summary>
    /// §750.8 — the button's COLOUR in words (operator 2026-08-01: <i>"include colors below label"</i>).
    /// </summary>
    /// <remarks>
    /// 🔑 The brief pairs a colour with each label, and the colour is what an attendee actually
    /// remembers — nobody recalls pressing "somewhat unhappy", they recall pressing the yellow one.
    /// Naming it also makes the table readable when the PDF is printed in greyscale, which a swatch
    /// alone is not.
    /// </remarks>
    public static string ColourName(int rating) => rating switch
    {
        4 => "dark green",
        3 => "light green",
        2 => "yellow",
        1 => "red",
        _ => string.Empty,
    };

    /// <summary>Rounded-rectangle fill, used for the hero panel and the callouts.</summary>
    public static void Panelled(XGraphics gfx, double x, double y, double w, double h, XColor fill,
                               double radius = 8)
        => gfx.DrawRoundedRectangle(new XSolidBrush(fill), x, y, w, h, radius * 2, radius * 2);
}

