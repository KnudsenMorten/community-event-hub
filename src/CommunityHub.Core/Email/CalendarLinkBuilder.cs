namespace CommunityHub.Core.Email;

/// <summary>
/// Builds "add to calendar that OPENS the entry" web URLs (Google Calendar +
/// Outlook web compose) for a single event. Unlike an .ics download — which a
/// managed browser tends to save to disk rather than hand to the calendar app —
/// these links open a pre-filled event in the browser, which is the behaviour the
/// operator asked for ("must open the entry, not download"). Times are emitted in
/// UTC. Use the .ics route only as an Apple/other fallback.
/// </summary>
public static class CalendarLinkBuilder
{
    /// <summary>Google Calendar "create event" template URL (opens in the browser).</summary>
    public static string GoogleUrl(
        string title, DateTimeOffset startUtc, DateTimeOffset endUtc,
        string? details = null, string? location = null)
    {
        var s = startUtc.UtcDateTime;
        var e = endUtc.UtcDateTime;
        var url =
            "https://calendar.google.com/calendar/render?action=TEMPLATE"
            + $"&text={Uri.EscapeDataString(title)}"
            + $"&dates={s:yyyyMMdd'T'HHmmss'Z'}/{e:yyyyMMdd'T'HHmmss'Z'}";
        if (!string.IsNullOrWhiteSpace(details)) url += $"&details={Uri.EscapeDataString(details)}";
        if (!string.IsNullOrWhiteSpace(location)) url += $"&location={Uri.EscapeDataString(location)}";
        return url;
    }

    /// <summary>Outlook-web "compose event" deeplink (opens in the browser).</summary>
    public static string OutlookUrl(
        string title, DateTimeOffset startUtc, DateTimeOffset endUtc,
        string? details = null, string? location = null)
    {
        var s = startUtc.UtcDateTime;
        var e = endUtc.UtcDateTime;
        var url =
            "https://outlook.office.com/calendar/0/deeplink/compose?path=/calendar/action/compose&rru=addevent"
            + $"&subject={Uri.EscapeDataString(title)}"
            + $"&startdt={s:yyyy-MM-dd'T'HH:mm:ss'Z'}"
            + $"&enddt={e:yyyy-MM-dd'T'HH:mm:ss'Z'}";
        if (!string.IsNullOrWhiteSpace(details)) url += $"&body={Uri.EscapeDataString(details)}";
        if (!string.IsNullOrWhiteSpace(location)) url += $"&location={Uri.EscapeDataString(location)}";
        return url;
    }
}
