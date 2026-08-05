using CommunityHub.Core.Evaluation;
using PdfSharp.Drawing;

namespace CommunityHub.Core.Signage;

/// <summary>
/// §754 §6 — the signage colour vocabulary as CSS hex, and the ONLY place the screens get a colour.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The rating colours are DERIVED from <c>EvaluationReportLayout</c>, never re-typed.</b>
/// §754.2 is explicit about this: the wall and the speaker's own PDF describe the same sessions, and
/// a contradiction between two surfaces is the kind of thing people photograph. That layout class is
/// <c>internal</c> to Core and speaks PDFsharp's <see cref="XColor"/>, so this is the seam that
/// converts it for HTML — a second list of hexes would drift the first time a button colour moved.
/// (§754 conflict 3: rating 2 is YELLOW <c>#E4B400</c>, the device's third button — the spec's "light
/// red" was a typo the operator corrected.)</para>
///
/// <para>🔒 <b>The contrast rules are not taste, they override the current OptiSigns templates.</b>
/// Never white on Experts Live Green — the existing portrait template does exactly that at roughly
/// 2:1 and is unreadable from across a hall. Text on green is always DarkBlue (~5.9:1); on DarkBlue
/// and on Blue it is always white (~11:1 and ~3.7:1, the latter acceptable only because the type is
/// enormous). Those pairings are encoded here as ready-made background/foreground pairs so a view
/// cannot accidentally invent an unreadable combination.</para>
/// </remarks>
public static class SignagePalette
{
    /// <summary>Experts Live DarkBlue.</summary>
    public const string DarkBlue = "#1D3380";

    /// <summary>Experts Live Green. 🔒 White text on this is forbidden — see the class remarks.</summary>
    public const string Green = "#9AC06A";

    /// <summary>Experts Live Blue — the Session Feedback background.</summary>
    public const string Blue = "#008BD2";

    public const string White = "#FFFFFF";

    /// <summary>
    /// The four rating colours, taken from the PDF report's palette so the wall and the report can
    /// never disagree. 4 = dark green, 3 = light green, 2 = YELLOW, 1 = red.
    /// </summary>
    public static string RatingHex(int rating) => Hex(rating switch
    {
        4 => EvaluationReportLayout.Rating4,
        3 => EvaluationReportLayout.Rating3,
        2 => EvaluationReportLayout.Rating2,
        1 => EvaluationReportLayout.Rating1,
        _ => EvaluationReportLayout.Muted,
    });

    /// <summary>
    /// The headline score's own colour, on the same green → yellow → red ramp as the buttons —
    /// the §750.8 decision, reused rather than re-derived.
    /// </summary>
    public static string ScoreHex(double? score) => Hex(EvaluationReportLayout.BandColour(score));

    /// <summary>
    /// The background/foreground pair for a view, per the §6 table. Returning them TOGETHER is the
    /// point: the unreadable combinations are unreachable rather than merely discouraged.
    /// </summary>
    public static (string Background, string Foreground) ViewColours(SignageView view) => view switch
    {
        // Cards are green on this, and their text is DarkBlue — never white.
        SignageView.Now => (DarkBlue, White),
        // Background is green ⇒ the header text on it must be DarkBlue, not white.
        SignageView.Next => (Green, DarkBlue),
        SignageView.Feedback => (Blue, White),
        _ => (DarkBlue, White),
    };

    /// <summary>The card's background/foreground pair for a view (§6): the inverse of the page.</summary>
    public static (string Background, string Foreground) CardColours(SignageView view) => view switch
    {
        SignageView.Now => (Green, DarkBlue),        // green card, DarkBlue text (~5.9:1)
        SignageView.Next => (DarkBlue, White),       // DarkBlue card, white text (~11:1)
        SignageView.Feedback => (White, DarkBlue),   // white panel so the ratings keep their own colours
        _ => (White, DarkBlue),
    };

    private static string Hex(XColor c) =>
        $"#{(int)c.R:X2}{(int)c.G:X2}{(int)c.B:X2}";
}

/// <summary>§754 §2 — the three views a screen cycles through.</summary>
public enum SignageView
{
    Now = 0,
    Next = 1,
    Feedback = 2,
}

/// <summary>§754 §2 — the two physical screen shapes (1080×1920 and 3840×2160).</summary>
public enum SignageOrientation
{
    Portrait = 0,
    Landscape = 1,
}
