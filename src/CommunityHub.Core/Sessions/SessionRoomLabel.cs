using System.Text.RegularExpressions;

namespace CommunityHub.Core.Sessions;

/// <summary>
/// §1167.4 — turn a raw room name into what an attendee should read.
/// </summary>
/// <remarks>
/// <para>Reviewer 2026-09-02, on <c>/Sessions</c>: <i>"Double 'Room Room' naming is showing."</i></para>
///
/// <para>🔑 <b>Neither side was wrong on its own.</b> The page labels the value <c>Room</c>, which is
/// right when a room is called "A3". The room names imported from the event platform are
/// <c>Room-A3-Floor 0-Max 250-MC</c> — they already say "Room", and they carry the floor, the
/// capacity and an internal flag as well. Put together they read <i>"Room Room-A3-Floor 0-Max
/// 250-MC"</i>.</para>
///
/// <para>🔒 <b>Fixed in the DISPLAY, not in the data.</b> The name is the platform's own and is
/// matched on elsewhere — the room filter on the sessions page uses the stored value, and rewriting
/// it at import would break that quietly. This is a rendering concern, so it is solved where the
/// rendering happens.</para>
/// </remarks>
public static class SessionRoomLabel
{
    /// <summary>
    /// Leading "Room" plus its separator — a dash, a space, a colon, or a mix.
    /// </summary>
    /// <remarks>
    /// ⚠️ Anchored, and it requires a SEPARATOR. Without that, "Rooftop" starts with "Room"… it does
    /// not, but "Room21" would be shortened to "21" while "Room 21" and "Room-21" are the intended
    /// hits — so the boundary is made explicit rather than left to chance.
    /// </remarks>
    private static readonly Regex LeadingRoom = new(
        @"^\s*room\s*[-–:]?\s+|^\s*room\s*[-–:]\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The room as it should appear beside a "Room" label.
    /// </summary>
    /// <remarks>
    /// 🔒 Returns the ORIGINAL when stripping would leave nothing. A room genuinely called "Room"
    /// must render as "Room", not as an empty tag that looks like missing data.
    /// </remarks>
    public static string ForDisplay(string? room)
    {
        var trimmed = (room ?? string.Empty).Trim();
        if (trimmed.Length == 0) return string.Empty;

        var stripped = LeadingRoom.Replace(trimmed, string.Empty, 1).Trim();
        return stripped.Length == 0 ? trimmed : stripped;
    }
}
