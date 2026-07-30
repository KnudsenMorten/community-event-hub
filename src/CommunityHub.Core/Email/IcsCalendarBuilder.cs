using System.Globalization;
using System.Text;

namespace CommunityHub.Core.Email;

/// <summary>
/// Minimal RFC 5545 iCalendar builder. <see cref="BuildVEvent"/> emits a single
/// VEVENT with METHOD:REQUEST — the one shape used by every e-mailed calendar
/// INVITATION (dinner, hotel, group photo, master class, and the §193 task /
/// session "Add Reminder" invites). Every VEVENT carries a stable UID so a
/// re-send updates the existing entry rather than duplicating it. The old
/// METHOD:PUBLISH <c>BuildFeed</c> (the subscribable per-user feed + .ics
/// downloads) was removed with §193.
///
/// <para>§234 6: for calendar clients to treat a METHOD:REQUEST as a REAL invitation
/// (auto-add / Accept–Decline processing in Outlook &amp; Gmail), the VEVENT must name
/// the RECIPIENT as an <c>ATTENDEE</c> and the sender (the hub's from-address) as the
/// <c>ORGANIZER</c> — a REQUEST whose organizer IS the recipient, or with no attendee,
/// is not processed as an invite. Pass <c>attendeeEmail</c>/<c>attendeeName</c> for
/// every recipient-addressed invite; they are optional only so legacy call sites keep
/// compiling.</para>
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
        string organizerName,
        bool allDay = false,
        string? attendeeEmail = null,
        string? attendeeName = null)
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
        if (allDay)
        {
            // All-day invite (e.g. a task-due-date reminder): DATE value, DTEND exclusive.
            AppendCrlf(sb, $"DTSTART;VALUE=DATE:{FmtDate(startUtc)}");
            AppendCrlf(sb, $"DTEND;VALUE=DATE:{FmtDate(endUtc)}");
        }
        else
        {
            AppendCrlf(sb, $"DTSTART:{FmtUtc(startUtc)}");
            AppendCrlf(sb, $"DTEND:{FmtUtc(endUtc)}");
        }
        AppendCrlf(sb, $"SUMMARY:{Escape(summary)}");
        AppendCrlf(sb, $"DESCRIPTION:{Escape(description)}");
        AppendCrlf(sb, $"LOCATION:{Escape(location)}");
        AppendCrlf(sb, $"ORGANIZER;CN={Escape(organizerName)}:mailto:{organizerEmail}");
        if (!string.IsNullOrWhiteSpace(attendeeEmail))
        {
            // §234 6: the recipient as a required participant awaiting action — this is
            // what makes mail clients render Accept/Decline and add the entry. RSVP=FALSE:
            // replies to the hub's from-mailbox are not processed, so don't solicit them.
            AppendCrlf(sb,
                $"ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=FALSE;"
                + $"CN={Escape(string.IsNullOrWhiteSpace(attendeeName) ? attendeeEmail : attendeeName)}"
                + $":mailto:{attendeeEmail}");
        }
        AppendCrlf(sb, "STATUS:CONFIRMED");
        AppendCrlf(sb, "SEQUENCE:0");
        AppendCrlf(sb, "END:VEVENT");
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
}
