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
/// §1077 (operator 2026-08-11: "/Organizer/Attendees shows 1-day tickets as 'other'" and
/// "prints the raw enum names") — the organizer-facing label for a <see cref="TicketStatus"/>.
///
/// The enum member is named <c>Other</c> because the SYNC only knows "an active ticket that is
/// not the 2-day class" (<c>AttendeeTicketSyncService</c>); for this event that class IS the
/// 1-day ticket, which is why the summary tile has counted <c>Other</c> under "1-day tickets"
/// since §707.36. The tile and the table disagreed only in wording, and the developer spelling
/// won in the two places a reader looks first.
///
/// 🔑 The label is presentation only — the enum name stays the route/filter value, so saved
/// links and the <c>Ticket=</c> query key keep working.
/// </summary>
public static class TicketStatusDisplay
{
    /// <summary>Organizer-facing label: "2-day", "1-day", "No ticket".</summary>
    public static string Label(this TicketStatus status) => status switch
    {
        TicketStatus.TwoDay => "2-day",
        TicketStatus.Other  => "1-day",
        _                   => "No ticket",
    };

    /// <summary>
    /// §1077 — the same treatment for the column NEXT TO IT. <c>NotBooked</c> and
    /// <c>MultipleBookings</c> are developer spellings that were being shown to organizers on the
    /// very same rows as the ticket type; fixing one word and leaving its neighbour would have been
    /// a half-finished sentence.
    /// </summary>
    /// <remarks>
    /// 🔑 "Double-booked" rather than "Multiple bookings": it is the word an organizer uses for the
    /// problem, and the row exists because somebody has to fix it.
    /// </remarks>
    public static string Label(this MasterClassBookingStatus status) => status switch
    {
        MasterClassBookingStatus.Booked           => "Booked",
        MasterClassBookingStatus.MultipleBookings => "Double-booked",
        _                                         => "Not booked",
    };
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
/// synced one-way from Zoho Backstage orders + tickets by the single authoritative
/// AttendeeBackstageSyncJob (REQUIREMENTS §125) and are read-only from the hub's
/// point of view (CEH never writes back to Zoho).
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

    /// <summary>
    /// 🔒 §707.23 — the address this ticket was held by BEFORE the most recent reassignment,
    /// lower-cased and trimmed. Null when the ticket has never moved.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-30: *"ceh must align 100% to zoho method … we keep old references when
    /// cancellations or reassignments happen"*. Cancellation already complied — the row is kept with
    /// <see cref="MirrorState"/> Cancelled and <see cref="CancelledAt"/> stamped. REASSIGNMENT did
    /// not: the row is keyed on <see cref="BackstageTicketId"/>, so a reassignment rewrote
    /// <see cref="Email"/> IN PLACE and the previous holder vanished without trace.</para>
    ///
    /// <para>🔑 It matters most for the bulk case he described: a manager buys 15 tickets on
    /// plus-addresses (<c>mok+eldk27-1@…</c>) and later assigns them to real people. Without this
    /// there is no way to answer *"which of my placeholders became this person?"* — the chain is
    /// simply gone.</para>
    ///
    /// <para>Deliberately ONE previous address, not a full history table: it answers the question
    /// actually asked, keeps Zoho's one-row-per-ticket shape, and the audit trail already records
    /// each reassignment event for anything deeper.</para>
    /// </remarks>
    public string? PreviousEmail { get; set; }

    /// <summary>§707.23 — when this ticket was last reassigned, or null if it never was.</summary>
    public DateTimeOffset? ReassignedAt { get; set; }

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

    /// <summary>
    /// §707.35b — Zoho's STABLE <c>ticket_class_id</c> for this ticket. The authoritative answer to
    /// "is this a 2-day (Master Class) ticket", per <see cref="MasterClassTicketPolicy"/>.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Operator 2026-07-30: *"ticket class has a unique number, dont use the displayname of the
    /// ticket class"*.</b> The NAME is an editable label — and it was edited (§447) — so anything
    /// keyed on it can be changed by a rename in Zoho. Mail audience must never hang on that.
    ///
    /// <para><b>Why this column had to exist.</b> The feed always carried the id
    /// (<c>ZohoClient</c> reads <c>ticket_class_id</c>) and the SYNC used it — but nothing persisted
    /// it, so every DOWNSTREAM consumer could only see the name. That is exactly how §707.34b's
    /// cancellation sweep ended up reaching for <see cref="TicketClassName"/>.</para>
    ///
    /// <para>Null on rows mirrored before this column existed, and on any payload Zoho hands us
    /// without an id; <see cref="MasterClassTicketPolicy"/> then falls back to the name markers, which
    /// is its documented behaviour — so historic rows keep resolving exactly as before.</para>
    /// </remarks>
    public string? TicketClassId { get; set; }

    // --- Mirror state (SOFT-CANCEL, §128) -----------------------------------
    /// <summary>
    /// Whether this ticket is still in Zoho's ACTIVE set (Active) or was
    /// cancelled/changed away upstream (Cancelled — SOFT-cancelled: the MC seat is
    /// released to the waitlist and the row is excluded from active counts, but the
    /// row + its history are KEPT). This is the NEW cancellation flag (REQUIREMENTS
    /// §128); it does NOT overload <see cref="TicketStatus"/>, which stays the 2-day
    /// Master-Class eligibility marker (§126).
    /// </summary>
    public MirrorState MirrorState { get; set; } = MirrorState.Active;

    /// <summary>When this ticket was soft-cancelled locally, or null while Active.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    // --- Backstage attendee + order details (REQUIREMENTS §6) ---------------
    /// <summary>
    /// The Backstage order id this ticket belongs to — also the foreign key to the
    /// order-level mirror (<see cref="Order"/>) via the (EventId, OrderId) →
    /// (EventId, BackstageOrderId) relationship. Null on legacy rows synced before
    /// the order mirror existed (REQUIREMENTS §125).
    /// </summary>
    public string? OrderId { get; set; }
    /// <summary>The order-level mirror this ticket belongs to (REQUIREMENTS §125); null
    /// when <see cref="OrderId"/> is unset (legacy rows).</summary>
    public Order? Order { get; set; }
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

    /// <summary>
    /// §234 3 — set when a ticket REASSIGNMENT put this attendee in need of the
    /// reassignment-VALIDATION email, cleared only when that email is actually
    /// DELIVERED (not ring-dropped, not failed). Persisting the intent makes the
    /// send retryable across sync runs: reassignment detection is one-shot (the
    /// mirror already holds the new email on the next pull), so without this marker
    /// a failed/dropped validation email was silently lost forever. Null = nothing
    /// pending. The inherited-MC title is NOT stored — it is recomputed at send time
    /// from the attendee's current confirmed signup, so a retry always states the
    /// CURRENT truth.
    /// </summary>
    public DateTimeOffset? ReassignmentValidationPendingSince { get; set; }

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
