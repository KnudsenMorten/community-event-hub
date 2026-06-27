namespace CommunityHub.Core.Domain;

/// <summary>
/// The CEH-side MIRROR STATE of a row that is reconciled one-way from Zoho
/// Backstage (REQUIREMENTS §125/§128). Distinct from <see cref="TicketStatus"/>
/// (which is the 2-day Master-Class eligibility marker, §126) and from a Zoho
/// status string: this is purely whether the local mirror still considers the
/// row part of Zoho's ACTIVE set.
///
/// SOFT-CANCEL (§128): a vanished/cancelled order or ticket is marked
/// <see cref="Cancelled"/> — the seat is released to the waitlist and it is
/// excluded from active counts — but the row + its history are KEPT, and CEH
/// NEVER writes/deletes anything back in Zoho.
/// </summary>
public enum MirrorState
{
    /// <summary>The row is present in Zoho's current (active) dataset.</summary>
    Active = 0,

    /// <summary>The row was cancelled / vanished upstream — soft-cancelled locally
    /// (history kept, excluded from active counts).</summary>
    Cancelled = 1
}

/// <summary>
/// An order-level MIRROR of a Zoho Backstage order (REQUIREMENTS §125). CEH holds
/// the FULL Zoho dataset — every order, every ticket class, not just 2-day — so
/// dashboards / telemetry / Master-Class eligibility can read trusted CEH SQL
/// instead of calling Zoho live (§127).
///
/// <para>Strictly ONE-WAY Zoho→CEH: this row is reconciled to match Zoho; CEH
/// never writes or deletes orders in Zoho. One <see cref="Order"/> has many
/// tickets/attendees (<see cref="Attendees"/>), linked by the Backstage order id.</para>
///
/// <para>Scoped to an edition by <see cref="EventId"/>. The natural identity is
/// (<see cref="EventId"/>, <see cref="BackstageOrderId"/>), which is also the
/// principal key the <see cref="Attendee"/>→Order foreign key targets.</para>
/// </summary>
public class Order
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- Identity (the Backstage order id) ----------------------------------
    /// <summary>The Zoho Backstage order id — the upsert key for this order within an edition.</summary>
    public string BackstageOrderId { get; set; } = string.Empty;

    // --- Buyer + billing (from the Backstage order) -------------------------
    /// <summary>The order buyer / billing name (Backstage order <c>billing_address.name</c> / contact).</summary>
    public string? BuyerName { get; set; }
    /// <summary>The order buyer email (lower-cased, trimmed when set).</summary>
    public string? BuyerEmail { get; set; }
    /// <summary>Company on the order (Backstage billing / contact <c>company_name</c>).</summary>
    public string? CompanyName { get; set; }
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

    // --- Status -------------------------------------------------------------
    /// <summary>Zoho's own order status string (e.g. "completed"/"cancelled"), for display / audit.
    /// Distinct from <see cref="MirrorState"/>, which is CEH's derived active/cancelled flag.</summary>
    public string? OrderStatus { get; set; }

    /// <summary>When the order was created IN ZOHO (the source timestamp), if known —
    /// distinct from <see cref="CreatedAt"/> (when the local mirror row was first written).</summary>
    public DateTimeOffset? SourceCreatedAt { get; set; }

    /// <summary>
    /// The full raw order JSON as pulled from Zoho, so no Backstage field is lost even
    /// before it has a dedicated column. Mapped to <c>nvarchar(max)</c> (TEXT on SQLite).
    /// </summary>
    public string? RawJson { get; set; }

    // --- Mirror state (SOFT-CANCEL, §128) -----------------------------------
    /// <summary>Whether this order is still in Zoho's ACTIVE set (Active) or was
    /// cancelled/vanished upstream (Cancelled — soft-cancelled, history kept).</summary>
    public MirrorState MirrorState { get; set; } = MirrorState.Active;

    /// <summary>When the order was soft-cancelled locally, or null while Active.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    // --- Sync bookkeeping ---------------------------------------------------
    /// <summary>When this row was last refreshed by the authoritative sync.</summary>
    public DateTimeOffset LastSyncedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the local mirror row was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // --- Tickets / attendees on this order ----------------------------------
    /// <summary>The tickets/attendees that belong to this order (linked by Backstage order id).</summary>
    public ICollection<Attendee> Attendees { get; set; } = new List<Attendee>();
}
