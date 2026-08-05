namespace CommunityHub.Core.Domain;

/// <summary>
/// §794 — a PREPAID allocation: how many tickets of ONE class a partner bought up front against one
/// coupon.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"lets say arrow dk buys 50 prepaid coupons. we need to deduct when
/// claimed so they know remaining"* … *"pool is linked to a ticket class 2 day"*.</para>
///
/// <para>🔑 <b>A COUNT of tickets, per ticket class — not a sum of money.</b> Arrow DK buys 50 × the
/// 2-day class. A partner holding two classes has two allocations, and they are spent independently:
/// a 2-day claim can never eat the 1-day pool.</para>
///
/// <para>🔒 <b>Only the PURCHASE is stored. The remaining balance is DERIVED, never accumulated</b>
/// (<see cref="Integrations.Erp.CouponPrepaidBalance"/>). A cancellation is a state change on a
/// ticket already in the order mirror, so a cancelled claim simply stops counting and the allocation
/// returns by itself — no release event, no compensating entry, and a sweep that is missed, repeated
/// or run twice produces the same number. A running total would drift the first time any of those
/// happened, and the drift would be invisible.</para>
///
/// <para>🔴 This is also what makes *"partner must not get a credit note"* satisfiable: the
/// correction for a cancelled prepaid claim is arithmetic on the current state, not a document.</para>
/// </remarks>
public class CouponPrepaidAllocation
{
    public int Id { get; set; }

    /// <summary>The edition this allocation belongs to.</summary>
    public int EventId { get; set; }

    /// <summary>The coupon row (the partner agreement) this allocation was bought against.</summary>
    public int CouponInvoicingSettingId { get; set; }

    /// <summary>
    /// 🔒 The Zoho <c>ticket_class_id</c>. Keyed on the ID and never the class NAME (§787.16): the
    /// live names carry double spaces and can be renamed in Backstage, which would silently move a
    /// partner's allocation to a different pool.
    /// </summary>
    public string TicketClassId { get; set; } = string.Empty;

    /// <summary>
    /// The class name as last seen in the order mirror — for DISPLAY only, so the page can say
    /// "2-day" rather than an 18-digit id. ⚠️ Never matched on.
    /// </summary>
    public string? TicketClassLabel { get; set; }

    /// <summary>
    /// §798.4 — the agreed purchases that make up this pool. <b>The quantity lives HERE, not on the
    /// pool</b>: a partner can buy 50 in January and 25 more in March under the same coupon code, and
    /// each purchase is invoiced separately.
    /// </summary>
    /// <remarks>
    /// 🔒 <c>purchased = SUM(Purchases)</c>, re-derived on every read. Topping up adds a row rather
    /// than editing a total, so which invoice covers which tickets stays answerable — and a mistake
    /// is a visible row instead of a number nobody can audit.
    /// </remarks>
    public ICollection<CouponPrepaidPurchase> Purchases { get; set; } = new List<CouponPrepaidPurchase>();

    /// <summary>
    /// Warn the organizer once the remaining balance reaches this. Null = use the service default.
    /// </summary>
    /// <remarks>
    /// ⚠️ A pool at zero means the next claimant gets a ticket nobody has paid for — discovered at
    /// the worst possible moment, by the attendee. The warning exists so it is discovered earlier.
    /// </remarks>
    public int? LowBalanceThreshold { get; set; }

    /// <summary>Free-text note (the PO number, what was agreed, who signed it).</summary>
    public string? Notes { get; set; }

    /// <summary>When the organizer was last warned this pool was running low. Null = never.</summary>
    public DateTimeOffset? LastLowBalanceAlertAt { get; set; }

    /// <summary>
    /// §796.2 — what <see cref="Integrations.Erp.CouponPoolBalance.Remaining"/> was the last time
    /// this pool was warned about. Null = never warned.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>This is what stops the quiet period hiding the moment it gets WORSE.</b> A pool alerted
    /// at "5 left" can be at "-2" an hour later, and that is a different fact — not a repeat. The
    /// alert therefore re-fires when the balance is LOWER than this, whatever the clock says.
    ///
    /// <para>⚠️ Recovery is deliberately silent: a cancellation putting tickets back needs no mail.
    /// Storing the NUMBER rather than a bool is what lets the service tell those two apart.</para>
    /// </remarks>
    public int? LastLowBalanceAlertRemaining { get; set; }

    /// <summary>
    /// §795.1 — the pool was closed BY A HUMAN. Null = not closed by hand (it may still read as
    /// closed because it is used in full — see <see cref="Integrations.Erp.CouponPoolState"/>).
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: *"we need a state of pool (open, closed when used in full)"*.</para>
    ///
    /// <para>🔴 <b>Two different closings, and only ONE of them is stored.</b> "Used in full" is
    /// arithmetic (<c>remaining &lt;= 0</c>) and is derived on every read, so it re-opens by itself
    /// the moment a ticket is cancelled — which is correct, because the allocation genuinely came
    /// back. An agreement a human ENDED is not arithmetic and must never re-open on its own: a
    /// cancellation inside a closed agreement would otherwise silently hand the partner back a
    /// ticket nobody agreed to.</para>
    ///
    /// <para>⚠️ Storing only the human decision is what keeps the two apart. A stored "closed" flag
    /// maintained by the sweep would drift exactly like an accumulated balance would.</para>
    /// </remarks>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Who closed it by hand. Null when it was never closed by hand.</summary>
    public string? ClosedByEmail { get; set; }

    /// <summary>Why it was closed early (the agreement ended, the partner is not taking the rest).</summary>
    public string? ClosedReason { get; set; }

    // ⚰️ §798.4 — the invoice number MOVED to CouponPrepaidPurchase, and the reason is commercial,
    // not tidiness: a pool topped up from 50 to 75 has TWO invoices, and one field could only ever
    // record one of them. A partner who bought 25 more would have read as fully billed because the
    // first 50 were invoiced in January. The migration moves the existing value onto the first
    // purchase row rather than dropping it.

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }

    /// <summary>Navigation to the coupon row.</summary>
    public CouponInvoicingSetting? CouponInvoicingSetting { get; set; }
}
