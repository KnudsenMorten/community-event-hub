using System.Text.RegularExpressions;
using CommunityHub.Core.Integrations;

namespace CommunityHub.Core.Reminders;

/// <summary>The reconciled state of one attendee email.</summary>
public sealed record AttendeeReconResult(
    string Email,
    string FirstName,
    string LastName,
    bool HasTwoDayTicket,
    string? TicketClassName,
    int MasterClassBookingCount,
    IReadOnlyList<string> MasterClassNames);

/// <summary>
/// Reconciles Zoho Backstage tickets against Zoho Bookings appointments
/// (CONTEXT.md 9z) - the C# port of the source PowerShell reconciliation.
/// Produces, per email, whether they hold a 2-day ticket and how many Master
/// Class seats they booked. The caller turns these into Attendee rows and the
/// three chaser reminders:
///   1. has 2-day ticket, 0 bookings  -> missing booking
///   2. has bookings, no 2-day ticket -> missing ticket
///   3. more than 1 booking           -> duplicate booking
/// </summary>
public sealed class AttendeeReconciler
{
    public IReadOnlyList<AttendeeReconResult> Reconcile(
        IReadOnlyList<ZohoTicket> tickets,
        IReadOnlyList<ZohoAppointment> appointments,
        string twoDayTicketRegex,
        string masterClassRegex)
    {
        var ticketRx = new Regex(
            StripInlineFlags(twoDayTicketRegex, out var ticketOpts), ticketOpts);
        var serviceRx = new Regex(
            StripInlineFlags(masterClassRegex, out var serviceOpts), serviceOpts);

        // Backstage: best ticket per email (a 2-day ticket wins).
        var ticketByEmail = new Dictionary<string, ZohoTicket>();
        var twoDayEmails = new HashSet<string>();
        foreach (var t in tickets)
        {
            if (string.IsNullOrWhiteSpace(t.Email)) continue;
            if (!ticketByEmail.ContainsKey(t.Email))
            {
                ticketByEmail[t.Email] = t;
            }
            if (ticketRx.IsMatch(t.TicketClassName ?? string.Empty))
            {
                twoDayEmails.Add(t.Email);
                ticketByEmail[t.Email] = t; // prefer the 2-day ticket
            }
        }

        // Bookings: active Master Class appointments grouped by email.
        var bookingsByEmail = new Dictionary<string, List<string>>();
        foreach (var a in appointments)
        {
            if (string.IsNullOrWhiteSpace(a.CustomerEmail)) continue;
            if (string.Equals(a.Status, "cancel",
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (!serviceRx.IsMatch(a.ServiceName ?? string.Empty)) continue;

            if (!bookingsByEmail.TryGetValue(a.CustomerEmail, out var list))
            {
                list = new List<string>();
                bookingsByEmail[a.CustomerEmail] = list;
            }
            list.Add(a.ServiceName ?? string.Empty);
        }

        // Union of all emails seen on either side.
        var allEmails = new HashSet<string>(ticketByEmail.Keys);
        allEmails.UnionWith(bookingsByEmail.Keys);

        var results = new List<AttendeeReconResult>();
        foreach (var email in allEmails)
        {
            ticketByEmail.TryGetValue(email, out var ticket);
            bookingsByEmail.TryGetValue(email, out var bookings);

            results.Add(new AttendeeReconResult(
                Email: email,
                FirstName: ticket?.FirstName ?? string.Empty,
                LastName: ticket?.LastName ?? string.Empty,
                HasTwoDayTicket: twoDayEmails.Contains(email),
                TicketClassName: ticket?.TicketClassName,
                MasterClassBookingCount: bookings?.Count ?? 0,
                MasterClassNames: bookings ?? new List<string>()));
        }

        return results;
    }

    /// <summary>
    /// .NET Regex does not accept inline "(?i)" the way the PowerShell source
    /// wrote it in all cases - lift a leading (?i) into RegexOptions.
    /// </summary>
    private static string StripInlineFlags(
        string pattern, out RegexOptions options)
    {
        options = RegexOptions.CultureInvariant;
        if (pattern.StartsWith("(?i)", StringComparison.Ordinal))
        {
            options |= RegexOptions.IgnoreCase;
            return pattern.Substring(4);
        }
        return pattern;
    }
}
