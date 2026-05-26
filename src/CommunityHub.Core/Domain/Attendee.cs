namespace CommunityHub.Core.Domain;

/// <summary>
/// Whether an attendee holds the ticket class that grants Master Class access.
/// </summary>
public enum TicketStatus
{
    /// <summary>No matching ticket found in Zoho Backstage orders.</summary>
    None = 0,

    /// <summary>Holds a 2-day ticket (Master Class access).</summary>
    TwoDay = 1,

    /// <summary>Holds some other ticket class (no Master Class access).</summary>
    Other = 2
}

/// <summary>
/// Whether the attendee has reserved a Master Class seat in Zoho Bookings.
/// </summary>
public enum MasterClassBookingStatus
{
    /// <summary>No active Master Class appointment in Zoho Bookings.</summary>
    NotBooked = 0,

    /// <summary>Exactly one active (non-cancelled) Master Class appointment.</summary>
    Booked = 1,

    /// <summary>
    /// More than one active Master Class appointment for this email - the
    /// attendee is double-booked and must cancel the extras. Detected by the
    /// reconciliation job; chased by its own reminder (CONTEXT.md 9z).
    /// </summary>
    MultipleBookings = 2
}

/// <summary>
/// An event attendee, reconciled from Zoho (CONTEXT.md 9z). Distinct from
/// <see cref="Participant"/>: organizer-managed participants are entered by organizers; attendees are
/// synced from Zoho Backstage orders + Zoho Bookings appointments by the
/// AttendeeReconcileJob and are read-only from the hub's point of view.
///
/// Scoped to an edition by EventId. Email is the identity used both for PIN
/// login to the attendee area and as the reconciliation key between the two
/// Zoho systems.
/// </summary>
public class Attendee
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- Identity (the reconciliation key) ----------------------------------
    /// <summary>Lower-cased, trimmed. The key that links a Backstage order to a Bookings appointment.</summary>
    public string Email { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;

    // --- Reconciled status --------------------------------------------------
    /// <summary>From Zoho Backstage orders - does this email hold a 2-day ticket?</summary>
    public TicketStatus TicketStatus { get; set; } = TicketStatus.None;

    /// <summary>The ticket class name as seen in Zoho, for display / audit.</summary>
    public string? TicketClassName { get; set; }

    /// <summary>From Zoho Bookings - has this email reserved a Master Class seat?</summary>
    public MasterClassBookingStatus BookingStatus { get; set; } = MasterClassBookingStatus.NotBooked;

    /// <summary>
    /// The Master Class / service name(s) booked, for display. A single name
    /// when Booked; a comma-separated list when MultipleBookings (so the
    /// attendee hub and the duplicate-chaser reminder can show all of them).
    /// Null when NotBooked.
    /// </summary>
    public string? MasterClassName { get; set; }

    /// <summary>
    /// True when this attendee is one half of a mismatch (2-day ticket but no
    /// booking, OR a booking but no 2-day ticket). Surfaced to organizers and
    /// drives the chaser reminders. Not auto-resolved - see CONTEXT.md 9z.
    /// </summary>
    public bool HasReconciliationMismatch { get; set; }

    // --- Sync bookkeeping ---------------------------------------------------
    /// <summary>When this row was last refreshed by the reconciliation job.</summary>
    public DateTimeOffset LastSyncedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
