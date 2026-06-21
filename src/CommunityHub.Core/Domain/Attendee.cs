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
    /// <summary>
    /// The STABLE Backstage ticket id — the real identity for the Master Class flow.
    /// A company can reassign a ticket to a different person (same id, new name/email);
    /// keying the attendee + their MC selection on this (not email) means the selection
    /// TRANSFERS to the new holder instead of orphaning. Null on legacy email-keyed rows.
    /// </summary>
    public string? BackstageTicketId { get; set; }

    /// <summary>Lower-cased, trimmed. A mutable attribute now (it changes on reassignment), not the key.</summary>
    public string Email { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    /// <summary>
    /// Full name = first + last (Zoho stores only first_name + last_name separately;
    /// this gives the combined form too, kept in sync on every Backstage sync).
    /// </summary>
    public string FullName { get; set; } = string.Empty;

    // --- Reconciled status --------------------------------------------------
    /// <summary>From Zoho Backstage orders - does this email hold a 2-day ticket?</summary>
    public TicketStatus TicketStatus { get; set; } = TicketStatus.None;

    /// <summary>The ticket class name as seen in Zoho, for display / audit.</summary>
    public string? TicketClassName { get; set; }

    // --- Backstage attendee + order details (REQUIREMENTS §6) ---------------
    /// <summary>The Backstage order id this ticket belongs to.</summary>
    public string? OrderId { get; set; }
    /// <summary>Attendee's company (Backstage contact <c>company_name</c>).</summary>
    public string? CompanyName { get; set; }
    /// <summary>Job title (Backstage contact <c>designation</c>).</summary>
    public string? JobTitle { get; set; }
    /// <summary>Phone (Backstage contact <c>mobile_no</c>).</summary>
    public string? Phone { get; set; }
    /// <summary>Country display name from the order billing address (e.g. "Denmark").</summary>
    public string? Country { get; set; }
    /// <summary>Country ISO code from the order billing address (e.g. "DK").</summary>
    public string? CountryCode { get; set; }
    /// <summary>City from the order billing address.</summary>
    public string? City { get; set; }
    /// <summary>Postcode from the order billing address.</summary>
    public string? Postcode { get; set; }
    /// <summary>Tax / VAT / CVR number from the order (<c>tax_registration_no</c>).</summary>
    public string? TaxId { get; set; }
    /// <summary>
    /// All Backstage contact CUSTOM fields (single_choice*, multiple_choice, …) as a
    /// JSON object, so every custom field is captured without a column per field.
    /// </summary>
    public string? CustomFieldsJson { get; set; }

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

    // --- Self-service link --------------------------------------------------
    /// <summary>
    /// Unguessable per-attendee token for the no-password Master Class self-service
    /// page (the emailed magic-link the attendee uses to join/leave an MC). Minted
    /// lazily; regenerating it revokes old links. URL-safe 256-bit secret.
    /// </summary>
    public string? SelfServiceToken { get; set; }

    /// <summary>
    /// When the "choose your Master Class" selection-invite email was sent to this
    /// attendee (tracked per user, sent vs not-sent). Null = not yet invited.
    /// </summary>
    public DateTimeOffset? MasterClassInviteSentAt { get; set; }

    // --- Self check-in ------------------------------------------------------
    /// <summary>
    /// When the attendee self-checked-in from their "My Event" dashboard, or
    /// null if they have not checked in. Self-service only: the attendee taps
    /// "I'm here" on-site (the hub never re-implements turnstiles or badge
    /// scanning - this is a lightweight presence signal the attendee owns).
    /// Set once; tapping again is a no-op (idempotent).
    /// </summary>
    public DateTimeOffset? CheckedInAt { get; set; }

    // --- Sync bookkeeping ---------------------------------------------------
    /// <summary>When this row was last refreshed by the reconciliation job.</summary>
    public DateTimeOffset LastSyncedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
