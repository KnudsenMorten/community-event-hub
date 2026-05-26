using System.Globalization;
using System.Text;

namespace CommunityHub.Core.Email;

/// <summary>
/// Minimal RFC 5545 iCalendar builder. Emits a single VEVENT with a stable
/// UID so re-sends with the same UID update the existing entry in the user's
/// calendar (instead of creating duplicates).
/// </summary>
public static class IcsCalendarBuilder
{
    public static string BuildVEvent(
        string uid,
        string summary,
        string description,
        string location,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string organizerEmail,
        string organizerName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//ExpertsLive Denmark//CommunityHub//EN");
        sb.AppendLine("METHOD:REQUEST");
        sb.AppendLine("CALSCALE:GREGORIAN");
        sb.AppendLine("BEGIN:VEVENT");
        sb.AppendLine($"UID:{uid}");
        sb.AppendLine($"DTSTAMP:{Fmt(DateTimeOffset.UtcNow)}");
        sb.AppendLine($"DTSTART:{Fmt(startUtc)}");
        sb.AppendLine($"DTEND:{Fmt(endUtc)}");
        sb.AppendLine($"SUMMARY:{Escape(summary)}");
        sb.AppendLine($"DESCRIPTION:{Escape(description)}");
        sb.AppendLine($"LOCATION:{Escape(location)}");
        sb.AppendLine($"ORGANIZER;CN={Escape(organizerName)}:mailto:{organizerEmail}");
        sb.AppendLine("STATUS:CONFIRMED");
        sb.AppendLine("SEQUENCE:0");
        sb.AppendLine("END:VEVENT");
        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    private static string Fmt(DateTimeOffset dt) =>
        dt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    // RFC 5545 escaping: backslash, comma, semicolon, newline.
    private static string Escape(string s) =>
        (s ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace(",", "\\,")
            .Replace(";", "\\;")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n");
}
