using System.Globalization;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §305 (operator 2026-07-24, CRITICAL timezone bug): "we use Danish timezone always …
/// you must store in the integration, if the different systems are presenting in
/// different timezones." The ONE authority for event-local time across the
/// integrations:
/// <list type="bullet">
/// <item><b>Sessionize</b> emits the event's LOCAL wall-clock with NO offset
/// ("2027-02-09T09:00:00"). The old parse assumed UTC, so every pushed session
/// landed +1h/+2h late in Zoho (9:00 Danish became "09:00Z" = 10:00 CET).</item>
/// <item><b>CEH</b> stores DateTimeOffset carrying the REAL Danish offset for that
/// date (CET +01:00 / CEST +02:00 — DST-correct).</item>
/// <item><b>Zoho Backstage</b> is configured to Europe/Copenhagen and its API takes
/// UTC ("…Z") — a correctly-offset CEH value converts exactly.</item>
/// </list>
/// </summary>
public static class EventTimezone
{
    /// <summary>Europe/Copenhagen, resolved with a Windows-id fallback ("Romance
    /// Standard Time") so both Linux and Windows hosts agree.</summary>
    public static readonly TimeZoneInfo Tz = Resolve();

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Europe/Copenhagen", "Romance Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* try the next id */ }
        }
        return TimeZoneInfo.Utc;   // last-resort — never expected on our hosts
    }

    /// <summary>
    /// Parse a timestamp from an integration source. A string WITH an explicit offset
    /// ("…Z", "+02:00") is honoured as-is; a NAIVE wall-clock string is interpreted as
    /// EVENT-LOCAL (Danish) time and gets the offset valid AT THAT DATE (CET/CEST).
    /// Null for blank/unparseable input.
    /// </summary>
    public static DateTimeOffset? ParseEventLocal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        // Explicit offset? 'Z' anywhere, or '+'/'-' AFTER the time part begins
        // (a '-' inside the date "2027-02-09" must not count).
        var timeStart = s.IndexOf('T');
        var hasOffset = s.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            || (timeStart > 0 && (s.IndexOf('+', timeStart) > 0 || s.IndexOf('-', timeStart) > 0));

        if (hasOffset)
        {
            return DateTimeOffset.TryParse(
                s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset)
                ? withOffset : null;
        }

        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
            return null;
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, Tz.GetUtcOffset(wall));
    }

    /// <summary>The event-local (Danish) display form for ops mails — the operator
    /// thinks in Danish time, never UTC.</summary>
    public static string ToEventLocalString(DateTimeOffset t) =>
        TimeZoneInfo.ConvertTime(t, Tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        + " (Danish time)";
}
