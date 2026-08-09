namespace CommunityHub.Core.Domain;

/// <summary>
/// §798.4 — ONE agreed purchase of prepaid tickets against a pool: "50 in January", "25 more in
/// March". Same coupon code, separate agreements, separate invoices.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"I must be able to extend a prepaid pool (increase amount) if customer
/// decides t buy more, but dont want a new coupon code. Then i will invoice him for fx 25 more as
/// invoice #."*</para>
///
/// <para>🔴 <b>This is why the quantity is not a number on the pool.</b> A pool that stored one
/// total could not answer the question his sentence ends on — <i>which</i> invoice covers <i>which</i>
/// tickets. Topping up would have meant editing a figure, losing the fact that two separate deals
/// happened and that only one of them has been billed.</para>
///
/// <para>🔒 <b>Adding tickets ADDS A ROW; it never adjusts a total.</b> The pool's purchased figure
/// is the SUM of these, re-derived on every read — the same rule that keeps the claimed side
/// un-driftable (§794.4). A mistake stays visible as a row somebody can look at, rather than being
/// baked into a number nobody can audit.</para>
///
/// <para>⚠️ <b>"Is this pool billed?" is per row, and that matters commercially.</b> A partner who
/// bought 25 more would otherwise look fully billed because the first 50 were invoiced in January —
/// so the §795.2 reminder chases the specific top-up that carries no invoice number.</para>
/// </remarks>
public class CouponPrepaidPurchase
{
    public int Id { get; set; }

    /// <summary>The pool (coupon × ticket class) these tickets were bought into.</summary>
    public int CouponPrepaidAllocationId { get; set; }

    /// <summary>How many tickets this particular agreement bought. Always positive.</summary>
    public int Quantity { get; set; }

    /// <summary>
    /// §795.2 — the e-conomic invoice number confirming THIS purchase was billed, typed in by a
    /// human. Null until somebody records it, and until then the pool is chased.
    /// </summary>
    /// <remarks>
    /// 🔒 A confirmation, not a link CEH validates: CEH never raises a prepaid invoice (§794), so
    /// nothing here can look it up. ⚠️ Text, not a number — it is quoted back to a partner, and a
    /// number typed with a prefix must survive verbatim.
    /// </remarks>
    public string? ErpInvoiceNumber { get; set; }

    /// <summary>
    /// 🔴 §1013a — TRUE once <see cref="ErpInvoiceNumber"/> is a BOOKED invoice number rather than
    /// a provisional draft number. Null/false ⇒ the number is a draft and can still change.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: *"bug: you mentioned invoice 182, but the actual number is 170"*,
    /// then *"i have sent invoice now and it got invoice 170"*.</para>
    ///
    /// <para>✅ <b>LIVE-VERIFIED against e-conomic (agreement 1685551, 2026-08-09):</b>
    /// <c>/invoices/drafts</c> held ONE draft (178, a webshop order) and <b>no</b>
    /// <c>CouponPrepaid-*</c> draft; <c>/invoices/booked</c> held <c>bookedInvoiceNumber</c>
    /// <b>170</b> for <c>CouponPrepaid-1</c>. <b>CEH had stored 182.</b></para>
    ///
    /// <para>🔑 <b>Nothing misread anything.</b> e-conomic numbers a DRAFT from one series and
    /// re-numbers it from another when a human books it, so the number CEH captured at creation was
    /// right at that moment and became a dead reference the instant he booked it. The defect was
    /// that nothing ever went back for the new one — and the warning was already written down, on
    /// <c>EconomicInvoiceReference.IsBooked</c>: *"A DRAFT number is provisional and is replaced
    /// when a human books the invoice."* Known, and not acted on.</para>
    ///
    /// <para>⚠️ This flag is why storing the bare digits is safe. He asked for "182", not
    /// "draft 182" — but the word was the only thing telling him the number was provisional, so
    /// dropping it WITHOUT this flag (and without
    /// <see cref="Integrations.Erp.CouponPrepaidInvoiceNumberRefresher"/> keeping it current) would
    /// have made a dead number look authoritative.</para>
    /// </remarks>
    public bool ErpInvoiceIsBooked { get; set; }

    public DateTimeOffset? ErpInvoiceConfirmedAt { get; set; }
    public string? ErpInvoiceConfirmedByEmail { get; set; }

    /// <summary>§795.2 — when this purchase was last chased for its missing invoice number.</summary>
    public DateTimeOffset? LastBillingReminderAt { get; set; }

    /// <summary>What was agreed, the PO number, who asked for it.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// §992 — the agreed unit price in DKK for THIS purchase, as the organizer typed it.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Stored because it cannot be recovered from anywhere else.</b> A claim invoice
    /// prices each ticket from Zoho's <c>base_price</c> on the claim; a prepaid purchase is agreed
    /// BEFORE anybody claims, and the rate is usually negotiated. Once the click is over, the number
    /// exists only on the e-conomic invoice — so without this column the page can list what was
    /// bought but never what it was worth.</para>
    ///
    /// <para>⚠️ Null for every purchase recorded before §992, and for one whose invoice was raised
    /// by hand. The aggregate says so rather than treating a missing price as 0 — a total that
    /// silently under-counts is worse than one that admits a gap.</para>
    /// </remarks>
    public decimal? UnitPriceDkk { get; set; }

    /// <summary>§992 — the agreed value of this purchase in DKK, or null when the price is unknown.</summary>
    public decimal? AgreedValueDkk => UnitPriceDkk is { } p ? p * Quantity : null;

    /// <summary>When the purchase was recorded — the clock the reminder's grace period runs on.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedByEmail { get; set; }

    public CouponPrepaidAllocation? Allocation { get; set; }

    /// <summary>True once somebody has recorded that this purchase was invoiced.</summary>
    public bool IsBilled => !string.IsNullOrWhiteSpace(ErpInvoiceNumber);
}
