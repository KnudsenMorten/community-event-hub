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

    public DateTimeOffset? ErpInvoiceConfirmedAt { get; set; }
    public string? ErpInvoiceConfirmedByEmail { get; set; }

    /// <summary>§795.2 — when this purchase was last chased for its missing invoice number.</summary>
    public DateTimeOffset? LastBillingReminderAt { get; set; }

    /// <summary>What was agreed, the PO number, who asked for it.</summary>
    public string? Notes { get; set; }

    /// <summary>When the purchase was recorded — the clock the reminder's grace period runs on.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CreatedByEmail { get; set; }

    public CouponPrepaidAllocation? Allocation { get; set; }

    /// <summary>True once somebody has recorded that this purchase was invoiced.</summary>
    public bool IsBilled => !string.IsNullOrWhiteSpace(ErpInvoiceNumber);
}
