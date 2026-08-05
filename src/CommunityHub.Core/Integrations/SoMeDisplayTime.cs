namespace CommunityHub.Core.Integrations;

/// <summary>
/// §844.5 — HOW A POST'S DATE AND TIME ARE SHOWN TO A HUMAN: Danish local, Danish format.
///
/// <para>Operator 2026-08-05: <i>"timeformat is danish time zone; not us date format and not in utc
/// time zone"</i>. Every organizer, sponsor and speaker surface renders through here.</para>
///
/// <para>🔒 <b>STORAGE STAYS UTC.</b> <c>SoMePost.ScheduledAtUtc</c> is UTC and must remain so —
/// this is a presentation concern only. Converting the stored value would be a far worse bug than
/// the one this fixes, and the planner reasons in UTC internally.</para>
///
/// <para>🔑 One place, not five. The calendar (§835), the Announced column (§836), the post editor
/// (§841) and the sponsor/speaker pages (§837/§838) all showed raw UTC in US format; five separate
/// fixes would have drifted apart the first time one of them changed.</para>
/// </summary>
public static class SoMeDisplayTime
{
    /// <summary>The instant as Danish local time.</summary>
    public static DateTimeOffset ToDanish(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, SoMeSchedulePlanner.DanishTime);

    /// <summary>Danish date: <c>05-08-2026</c> — day first, never the US month-first order.</summary>
    public static string Date(DateTimeOffset utc) => ToDanish(utc).ToString("dd-MM-yyyy");

    /// <summary>Danish date + 24-hour time: <c>05-08-2026 14:30</c>.</summary>
    public static string DateTime(DateTimeOffset utc) => ToDanish(utc).ToString("dd-MM-yyyy HH:mm");

    /// <summary>Just the time: <c>14:30</c>.</summary>
    public static string Time(DateTimeOffset utc) => ToDanish(utc).ToString("HH:mm");

    /// <summary>A short day for a compact column: <c>05-08</c>.</summary>
    public static string ShortDate(DateTimeOffset utc) => ToDanish(utc).ToString("dd-MM");

    /// <summary>A full heading for a calendar day, in Danish order: <c>Tuesday 05-08-2026</c>.</summary>
    public static string DayHeading(DateTimeOffset utc) =>
        ToDanish(utc).ToString("dddd dd-MM-yyyy");

    /// <summary>The DAY a post falls on in DANISH time — which is what groups a calendar.</summary>
    /// <remarks>
    /// ⚠️ Grouping by the UTC day put a 23:30-Danish post on the previous day's page. The whole
    /// point of grouping is "what goes out that day", so the day must be the local one.
    /// </remarks>
    public static DateOnly DanishDay(DateTimeOffset utc) =>
        DateOnly.FromDateTime(ToDanish(utc).DateTime);

    /// <summary>
    /// The value for an <c>&lt;input type="datetime-local"&gt;</c>, which is always local wall-clock —
    /// so it must be the DANISH wall-clock, not UTC.
    /// </summary>
    public static string ForInput(DateTimeOffset utc) =>
        ToDanish(utc).ToString("yyyy-MM-ddTHH:mm");

    /// <summary>
    /// Turns what he typed into that input (Danish wall-clock) back into the UTC instant to store.
    /// </summary>
    /// <remarks>
    /// 🔒 The inverse of <see cref="ForInput"/>, and they must stay a pair: reading Danish and writing
    /// UTC without converting would silently move every edited post by one or two hours.
    /// </remarks>
    public static DateTimeOffset FromInput(System.DateTime danishWallClock) =>
        SoMeSchedulePlanner.ToUtc(
            DateOnly.FromDateTime(danishWallClock),
            TimeOnly.FromDateTime(danishWallClock));
}
