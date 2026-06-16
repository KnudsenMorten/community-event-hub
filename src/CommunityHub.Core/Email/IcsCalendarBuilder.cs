using System.Globalization;
using System.Text;

namespace CommunityHub.Core.Email;

/// <summary>
/// One item to render as a VEVENT inside a calendar feed / file. Each item has
/// a stable <see cref="Uid"/> so a re-fetch UPDATES the existing entry in the
/// user's calendar (instead of creating a duplicate). All-day deadlines set
/// <see cref="AllDay"/> = true (DTSTART;VALUE=DATE); timed shifts set explicit
/// start/end. <see cref="AlarmsDaysBefore"/> emits one VALARM per entry.
/// </summary>
public sealed record CalendarItem(
    string Uid,
    string Summary,
    string? Description,
    string? Location,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    IReadOnlyList<int> AlarmsDaysBefore);

/// <summary>
/// Minimal RFC 5545 iCalendar builder. Two shapes:
///  - <see cref="BuildVEvent"/> emits a single VEVENT with METHOD:REQUEST, used
///    by the e-mailed hotel invite (an explicit meeting request).
///  - <see cref="BuildFeed"/> emits a METHOD:PUBLISH VCALENDAR with N VEVENTs,
///    used by the subscribable per-user calendar feed (GET /calendar/{token}.ics).
/// Every VEVENT carries a stable UID so updates replace, not duplicate; deadline
/// items can carry VALARM reminders (e.g. 7 and 1 days before due).
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
        AppendCrlf(sb, "BEGIN:VCALENDAR");
        AppendCrlf(sb, "VERSION:2.0");
        AppendCrlf(sb, "PRODID:-//ExpertsLive Denmark//CommunityHub//EN");
        AppendCrlf(sb, "METHOD:REQUEST");
        AppendCrlf(sb, "CALSCALE:GREGORIAN");
        AppendCrlf(sb, "BEGIN:VEVENT");
        AppendCrlf(sb, $"UID:{uid}");
        AppendCrlf(sb, $"DTSTAMP:{FmtUtc(DateTimeOffset.UtcNow)}");
        AppendCrlf(sb, $"DTSTART:{FmtUtc(startUtc)}");
        AppendCrlf(sb, $"DTEND:{FmtUtc(endUtc)}");
        AppendCrlf(sb, $"SUMMARY:{Escape(summary)}");
        AppendCrlf(sb, $"DESCRIPTION:{Escape(description)}");
        AppendCrlf(sb, $"LOCATION:{Escape(location)}");
        AppendCrlf(sb, $"ORGANIZER;CN={Escape(organizerName)}:mailto:{organizerEmail}");
        AppendCrlf(sb, "STATUS:CONFIRMED");
        AppendCrlf(sb, "SEQUENCE:0");
        AppendCrlf(sb, "END:VEVENT");
        AppendCrlf(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>
    /// Build a METHOD:PUBLISH VCALENDAR containing one VEVENT per item. This is
    /// the body of the subscribable feed and the one-off "download .ics".
    /// <paramref name="calendarName"/> sets X-WR-CALNAME so the subscription
    /// shows a friendly name in the client. The owner's e-mail is used for the
    /// ORGANIZER / ATTENDEE so the events resolve to the right person.
    /// </summary>
    public static string BuildFeed(
        string calendarName,
        string ownerEmail,
        string ownerName,
        IEnumerable<CalendarItem> items)
    {
        var sb = new StringBuilder();
        AppendCrlf(sb, "BEGIN:VCALENDAR");
        AppendCrlf(sb, "VERSION:2.0");
        AppendCrlf(sb, "PRODID:-//ExpertsLive Denmark//CommunityHub//EN");
        AppendCrlf(sb, "METHOD:PUBLISH");
        AppendCrlf(sb, "CALSCALE:GREGORIAN");
        AppendCrlf(sb, FoldLine($"X-WR-CALNAME:{Escape(calendarName)}"));
        AppendCrlf(sb, "X-PUBLISHED-TTL:PT6H");
        AppendCrlf(sb, "REFRESH-INTERVAL;VALUE=DURATION:PT6H");

        var stamp = FmtUtc(DateTimeOffset.UtcNow);
        var hasOwner = !string.IsNullOrWhiteSpace(ownerEmail);

        foreach (var item in items)
        {
            AppendCrlf(sb, "BEGIN:VEVENT");
            AppendCrlf(sb, FoldLine($"UID:{item.Uid}"));
            AppendCrlf(sb, $"DTSTAMP:{stamp}");
            if (item.AllDay)
            {
                // All-day event: DATE value, DTEND exclusive (next day).
                AppendCrlf(sb, $"DTSTART;VALUE=DATE:{FmtDate(item.Start)}");
                AppendCrlf(sb, $"DTEND;VALUE=DATE:{FmtDate(item.End)}");
            }
            else
            {
                AppendCrlf(sb, $"DTSTART:{FmtUtc(item.Start)}");
                AppendCrlf(sb, $"DTEND:{FmtUtc(item.End)}");
            }
            AppendCrlf(sb, FoldLine($"SUMMARY:{Escape(item.Summary)}"));
            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                AppendCrlf(sb, FoldLine($"DESCRIPTION:{Escape(item.Description)}"));
            }
            if (!string.IsNullOrWhiteSpace(item.Location))
            {
                AppendCrlf(sb, FoldLine($"LOCATION:{Escape(item.Location)}"));
            }
            if (hasOwner)
            {
                AppendCrlf(sb, FoldLine(
                    $"ORGANIZER;CN={Escape(ownerName)}:mailto:{ownerEmail}"));
                AppendCrlf(sb, FoldLine(
                    "ATTENDEE;CN=" + Escape(ownerName) +
                    ";PARTSTAT=ACCEPTED;RSVP=FALSE:mailto:" + ownerEmail));
            }
            AppendCrlf(sb, "STATUS:CONFIRMED");
            AppendCrlf(sb, "TRANSP:TRANSPARENT");
            AppendCrlf(sb, "SEQUENCE:0");

            foreach (var days in item.AlarmsDaysBefore ?? Array.Empty<int>())
            {
                if (days < 0) continue;
                AppendCrlf(sb, "BEGIN:VALARM");
                AppendCrlf(sb, "ACTION:DISPLAY");
                AppendCrlf(sb, FoldLine($"DESCRIPTION:{Escape(item.Summary)}"));
                // Negative => before the event start. P0D for same-day.
                AppendCrlf(sb, $"TRIGGER:-P{days}D");
                AppendCrlf(sb, "END:VALARM");
            }

            AppendCrlf(sb, "END:VEVENT");
        }

        AppendCrlf(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    // RFC 5545 mandates CRLF line endings.
    private static void AppendCrlf(StringBuilder sb, string line) =>
        sb.Append(line).Append("\r\n");

    private static string FmtUtc(DateTimeOffset dt) =>
        dt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string FmtDate(DateTimeOffset dt) =>
        dt.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    // RFC 5545 escaping: backslash, comma, semicolon, newline.
    private static string Escape(string? s) =>
        (s ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace(",", "\\,")
            .Replace(";", "\\;")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n");

    // RFC 5545 §3.1: content lines SHOULD be folded at 75 octets. Calendar
    // clients are lenient, but Outlook in particular can choke on very long
    // unfolded lines — fold conservatively at 73 chars + CRLF + a leading space.
    private static string FoldLine(string line)
    {
        const int max = 73;
        if (line.Length <= max) return line;
        var sb = new StringBuilder(line.Length + line.Length / max + 4);
        var i = 0;
        sb.Append(line.AsSpan(0, max));
        i = max;
        while (i < line.Length)
        {
            var take = Math.Min(max, line.Length - i);
            sb.Append("\r\n ").Append(line.AsSpan(i, take));
            i += take;
        }
        return sb.ToString();
    }
}
