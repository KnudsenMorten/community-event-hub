namespace CommunityHub.Core.Domain.Signage;

/// <summary>
/// §754 §10 — one row per edition holding everything an organiser controls about the venue screens:
/// the two access tokens, the six on/off switches, the daily schedule, and the tunable layout and
/// timing values.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The token IS the credential.</b> An OptiSigns player cannot log in, so the pages are
/// anonymous and gated on an unguessable <c>?t=</c>. One token per ORIENTATION (not per screen and
/// not per view), because that is the unit the operator actually manages: one playlist per
/// orientation, one URL to paste, one thing to rotate if a URL leaks.</para>
///
/// <para>🔒 <b>Every "no" renders a HOLDING SCREEN at HTTP 200</b> — switched off, outside the
/// schedule, wrong token, no token. Never a 401, never a 404, never an error page. OptiSigns has no
/// way to tell a human that an asset failed; it just shows whatever came back, so an error page IS
/// the failure mode, displayed two metres tall. A holding screen with the logo is indistinguishable
/// from intended behaviour to a passer-by, which is exactly what is wanted.</para>
///
/// <para>🔑 <b>The schedule is interpreted as a daily window inside an optional date range</b>
/// (§10: "so pages serve content during event hours and switch off automatically outside them").
/// The spec locks the requirement, not its shape; a daily from/to plus active-from/to covers "on
/// during event hours on event days" with two values an organiser can reason about, and every field
/// is nullable — unset means "no restriction", so a blank schedule never silently blanks the wall.
/// </para>
/// </remarks>
public class SignageSettings
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The portrait playlist's token (1080×1920 screens).</summary>
    public string PortraitToken { get; set; } = string.Empty;

    /// <summary>The landscape playlist's token (3840×2160 screens).</summary>
    public string LandscapeToken { get; set; } = string.Empty;

    /// <summary>UTC. When the tokens were last regenerated — shown beside the URLs so the operator
    /// can tell a stale pasted playlist from a current one.</summary>
    public DateTimeOffset? TokensRotatedAt { get; set; }

    // --- on/off, per view AND per orientation (§10) --------------------------
    // Six switches rather than three, because the two walls are physically different: a screen may
    // be pinned to one view, and a portrait pillar showing the feedback score is a different
    // decision from the landscape wall showing it.

    public bool PortraitNowEnabled { get; set; } = true;
    public bool PortraitNextEnabled { get; set; } = true;
    public bool PortraitFeedbackEnabled { get; set; } = true;
    public bool LandscapeNowEnabled { get; set; } = true;
    public bool LandscapeNextEnabled { get; set; } = true;
    public bool LandscapeFeedbackEnabled { get; set; } = true;

    // --- schedule (venue-local; null = no restriction) -----------------------

    /// <summary>Venue-local time of day the screens start serving content. Null = no daily floor.</summary>
    public TimeOnly? DailyFromLocal { get; set; }

    /// <summary>Venue-local time of day they stop. Null = no daily ceiling.</summary>
    public TimeOnly? DailyToLocal { get; set; }

    /// <summary>First venue-local date the screens serve content. Null = no start date.</summary>
    public DateOnly? ActiveFromLocal { get; set; }

    /// <summary>Last venue-local date (inclusive). Null = no end date.</summary>
    public DateOnly? ActiveToLocal { get; set; }

    // --- timing + layout (§6/§7 "treat as proportional guidance, make configurable") ----

    /// <summary>Seconds each of the three views is shown by the rotator (§3, default 10).</summary>
    public int RotateSeconds { get; set; } = 10;

    /// <summary>
    /// Seconds each internal PAGE is shown when a slot overflows (§7, default 5 on both
    /// orientations — the landscape template's current 3 is to be changed to 5).
    /// </summary>
    /// <remarks>
    /// 🔑 The two timers must not interfere: the view rotation advances on its own cycle regardless
    /// of which internal page is showing. They are separate values here for that reason.
    /// </remarks>
    public int PageSeconds { get; set; } = 5;

    /// <summary>Portrait grid: 2 columns × 4 rows = 8 cards per page (§6).</summary>
    public int PortraitColumns { get; set; } = 2;
    public int PortraitRows { get; set; } = 4;

    /// <summary>Landscape grid: 5 columns × 3 rows = 15 cards per page (§6).</summary>
    public int LandscapeColumns { get; set; } = 5;
    public int LandscapeRows { get; set; } = 3;

    /// <summary>Gap between cards, portrait / landscape (§6: 10 and 35).</summary>
    public int PortraitGap { get; set; } = 10;
    public int LandscapeGap { get; set; } = 35;

    /// <summary>Extra gap between grid ROWS, portrait / landscape (§6: 30 and 80).</summary>
    public int PortraitRowGap { get; set; } = 30;
    public int LandscapeRowGap { get; set; } = 80;
}
