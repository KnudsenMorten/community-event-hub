namespace CommunityHub.Core.Domain;

/// <summary>
/// THE single rule for "does this Backstage ticket class grant Master Class access?"
///
/// Master Classes are pre-day deep-dives bundled into the multi-day ticket, so a
/// ticket grants access when its class NAME contains one of a configurable list of
/// markers (<see cref="DefaultMarkers"/>). For ELDK27 the marker is <c>"2-day"</c>,
/// so e.g. <c>"2-day Pre-day + Main Event"</c> matches but a single-day ticket does
/// not. Matching is case-insensitive and space-insensitive, so <c>"2 day"</c>,
/// <c>"2-Day"</c> and <c>"2-day"</c> all count.
///
/// Centralised here so the attendee sync (eligibility), the Welcome page (whether to
/// show Master Class content) and any future caller share ONE definition — change
/// the markers in one place to onboard a differently-named ticket.
/// </summary>
public static class MasterClassTicketPolicy
{
    /// <summary>
    /// Ticket-class name markers that grant Master Class access. ELDK27 uses
    /// "2-day" (the only multi-day class includes the pre-day Master Classes).
    /// "2 day" is the same marker after space-normalisation but listed for clarity.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultMarkers = new[] { "2-day", "2 day" };

    /// <summary>
    /// True when <paramref name="ticketClassName"/> contains any Master-Class marker.
    /// Both the ticket name and the markers are compared with spaces removed and
    /// case ignored, so "2-day Pre-day + Main Event" matches "2-day"/"2 day".
    /// Pass <paramref name="markers"/> to override the default list (operator config).
    /// </summary>
    public static bool IncludesMasterClass(
        string? ticketClassName, IEnumerable<string>? markers = null)
    {
        if (string.IsNullOrWhiteSpace(ticketClassName)) return false;
        var hay = ticketClassName.Replace(" ", string.Empty);
        foreach (var m in markers ?? DefaultMarkers)
        {
            if (string.IsNullOrWhiteSpace(m)) continue;
            var needle = m.Replace(" ", string.Empty);
            if (hay.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
