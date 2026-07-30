namespace CommunityHub.Core.Domain;

/// <summary>
/// THE single rule for "does this Backstage ticket class grant Master Class access?"
///
/// Master Classes are pre-day deep-dives bundled into the multi-day ticket. The rule resolves in
/// TWO steps, in this order:
/// <list type="number">
/// <item><b>Ticket class ID (authoritative).</b> Zoho carries a stable <c>ticket_class_id</c> on
///   each ticket. When the edition configures its two-day class id(s) and the ticket carries one,
///   the ID decides — and a class RENAME cannot change the answer.</item>
/// <item><b>Name markers (fallback).</b> When no id is configured, or the ticket has none, the
///   class NAME must contain one of <see cref="DefaultMarkers"/>. Matching is case- AND
///   space-insensitive, so <c>"2 day"</c>, <c>"2-Day"</c> and <c>"2-day"</c> all count.</item>
/// </list>
///
/// <para>§447 (operator 2026-07-27) — he renamed the classes to <c>"2-day (Pre-day + Main Event)"</c>
/// and <c>"1-day (Main Event)"</c>, because buyers were reading the 1-day ticket as including the
/// pre-day, and asked that BOTH namings keep working since older orders carry the old text.
/// The substring rule already handled that — parentheses do not disturb a <c>"2-day"</c> match,
/// and a real renamed order on PROD classified as TwoDay — but a name is still a label a human can
/// edit. The ID path makes the next rename a non-event; the name path stays so historic orders,
/// and any ticket Zoho hands us without a class id, resolve exactly as before.</para>
///
/// <para>Centralised here so the attendee sync (eligibility), telemetry, the sign-in gate and the
/// task seeders share ONE definition.</para>
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
    /// True when the ticket grants Master Class access. <paramref name="ticketClassId"/> decides
    /// when it is present AND <paramref name="twoDayClassIds"/> is non-empty; otherwise the name
    /// markers decide. Passing no ids reproduces the pre-§447 behaviour exactly.
    /// </summary>
    /// <param name="ticketClassId">Zoho's stable <c>ticket_class_id</c>, or null when unknown.</param>
    /// <param name="ticketClassName">The class name, used when the id cannot decide.</param>
    /// <param name="twoDayClassIds">The edition's two-day class id(s) — per-edition config.</param>
    /// <param name="markers">Override for the name markers — per-edition config.</param>
    public static bool IncludesMasterClass(
        string? ticketClassId,
        string? ticketClassName,
        IEnumerable<string>? twoDayClassIds,
        IEnumerable<string>? markers = null)
    {
        var ids = twoDayClassIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToList();

        if (ids is { Count: > 0 } && !string.IsNullOrWhiteSpace(ticketClassId))
        {
            // A configured id set PLUS a ticket that carries an id is a complete answer, either
            // way. Returning the verdict here — rather than falling through to the name on a
            // non-match — is what makes the id authoritative: otherwise a stale or hand-edited
            // name could silently override the id we trust more.
            var got = ticketClassId.Trim();
            return ids.Any(id => string.Equals(id, got, StringComparison.OrdinalIgnoreCase));
        }

        // No usable id — historic orders, or a payload without a class id. Unchanged behaviour.
        return IncludesMasterClass(ticketClassName, markers);
    }

    /// <summary>
    /// Name-only overload: true when <paramref name="ticketClassName"/> contains any Master-Class
    /// marker. Both the ticket name and the markers are compared with spaces removed and case
    /// ignored, so BOTH <c>"2-day Pre-day + Main Event"</c> (old) and
    /// <c>"2-day (Pre-day + Main Event)"</c> (new, §447) match, while <c>"1-day (Main Event)"</c>
    /// does not.
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
