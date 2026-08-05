namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §6.4 / work-order §3.5 — the ONE place that names a generated logistics file.
/// </summary>
/// <remarks>
/// <para>Work order: <i>"All files lowercase, prefixed with the event short name (`eldk27`), which
/// is an <b>event-level setting, never a constant</b>."</i> So the prefix is passed in, from
/// <c>Event.Code</c> — a second edition on the same library must not overwrite the first one's
/// files, which is exactly what a hard-coded <c>eldk27</c> would do.</para>
///
/// <para>🔒 <b>Named here rather than at each producer</b>, for the reason §767 and §768.16 both
/// paid for: a file name that is composed in one place and looked for in another drifts, and the
/// drift is silent — the reader finds nothing and reports zero.</para>
/// </remarks>
public static class LogisticsFileNames
{
    /// <summary>Lower-cased, space- and slash-free — the shape every §3.5 name uses.</summary>
    public static string Slug(string? value)
    {
        var chars = (value ?? string.Empty).Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var joined = new string(chars);
        while (joined.Contains("--", StringComparison.Ordinal))
            joined = joined.Replace("--", "-", StringComparison.Ordinal);
        return joined.Trim('-');
    }

    private static string Prefix(string eventShortName)
    {
        var slug = Slug(eventShortName);
        // ⚠️ An empty prefix would produce "-polo.xlsx" and, worse, make two editions collide.
        // Falling back to "event" is visibly wrong rather than quietly wrong.
        return slug.Length == 0 ? "event" : slug;
    }

    // ---- Swag (§3.5) --------------------------------------------------------------------
    public static string Award(string eventShortName) => $"{Prefix(eventShortName)}-award.xlsx";
    public static string Polo(string eventShortName) => $"{Prefix(eventShortName)}-polo.xlsx";

    /// <summary>One pair per CEH role: <c>{prefix}-credly-{rolename}.xlsx</c> and <c>.csv</c>.</summary>
    public static string Credly(string eventShortName, string roleName, string extension) =>
        $"{Prefix(eventShortName)}-credly-{Slug(roleName)}{Normalise(extension)}";

    // ---- Hotel (§3.5) — one file per hotel engagement -----------------------------------
    public static string Hotel(string eventShortName, string hotelName) =>
        $"{Prefix(eventShortName)}-hotel-{Slug(hotelName)}.xlsx";

    // ---- Bella Center food (§3.5) — six files, one folder --------------------------------
    public static string Breakfast(string eventShortName, LogisticsDay day) =>
        $"{Prefix(eventShortName)}-breakfast-{DaySegment(day)}.xlsx";

    public static string Lunch(string eventShortName, LogisticsDay day) =>
        $"{Prefix(eventShortName)}-lunch-{DaySegment(day)}.xlsx";

    public static string AppreciationDinner(string eventShortName) =>
        $"{Prefix(eventShortName)}-appreciationdinner-preday.xlsx";

    public static string Party(string eventShortName) =>
        $"{Prefix(eventShortName)}-party-preday.xlsx";

    // ---- Bella Center expo (§3.5) — from webshop orders ----------------------------------
    public static string ExpoTvRental(string eventShortName) =>
        $"{Prefix(eventShortName)}-expo-rental-tv.xlsx";

    public static string ExpoFurnitureRental(string eventShortName) =>
        $"{Prefix(eventShortName)}-expo-rental-furniture.xlsx";

    /// <summary>
    /// The day segment exactly as §3.5 spells it — <c>day1-preday</c> / <c>day2-mainday</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Both halves are load-bearing. The venue reads these names, and "day1" alone would not tell
    /// them which day of the event it is; "preday" alone would not sort.
    /// </remarks>
    private static string DaySegment(LogisticsDay day) =>
        day == LogisticsDay.PreDay ? "day1-preday" : "day2-mainday";

    private static string Normalise(string extension)
    {
        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (ext.Length == 0) return ".xlsx";
        return ext.StartsWith('.') ? ext : "." + ext;
    }
}

/// <summary>Which event day a logistics file covers.</summary>
public enum LogisticsDay
{
    /// <summary>The pre-day (master classes) — "day1" in the venue's file names.</summary>
    PreDay = 0,

    /// <summary>The main day — "day2".</summary>
    MainDay = 1,
}
