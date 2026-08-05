using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §795.1 — what a pool IS right now. ⚠️ Two of these three are DERIVED and one is a decision.
/// </summary>
public enum CouponPoolState
{
    /// <summary>Tickets are left and nobody has ended the agreement.</summary>
    Open = 0,

    /// <summary>
    /// Used in full — <c>remaining &lt;= 0</c>. 🔒 DERIVED, so it re-opens by itself when a ticket
    /// is cancelled and the allocation genuinely comes back.
    /// </summary>
    UsedInFull = 1,

    /// <summary>
    /// Closed by a human: the agreement ended, the partner is not taking the rest. 🔴 This one must
    /// NEVER re-open on its own — no derivation can express "we agreed to stop".
    /// </summary>
    ClosedByHand = 2,
}

/// <summary>
/// §794 — one prepaid pool as a partner would read it: bought, used, left.
/// </summary>
/// <param name="TicketClassId">The Zoho class this pool is for.</param>
/// <param name="Label">The class name, for display only.</param>
/// <param name="Purchased">What the partner bought up front.</param>
/// <param name="Claimed">Live claims against it — cancelled tickets excluded, which is what releases them.</param>
/// <param name="LowBalanceThreshold">The level at which this pool is worth warning about.</param>
/// <param name="Purchased">
/// §798.4 — the SUM of every agreed purchase on this pool, not a stored total. "50 in January plus
/// 25 in March" is 75 here and two rows underneath.
/// </param>
/// <param name="ClosedByHand">§795.1 — a human ended this agreement.</param>
/// <param name="UnbilledPurchases">
/// §795.2/§798.4 — how many of those purchases carry no e-conomic invoice number. ⚠️ Counted per
/// PURCHASE: a pool topped up after being invoiced once is not billed, and a single flag would have
/// said it was.
/// </param>
/// <param name="AllocationId">
/// The row this balance was derived from, so a page can act on it (close, re-open, top up). 0 when
/// the balance was built without one.
/// </param>
public sealed record CouponPoolBalance(
    string TicketClassId,
    string? Label,
    int Purchased,
    int Claimed,
    int LowBalanceThreshold,
    bool ClosedByHand = false,
    int UnbilledPurchases = 0,
    int AllocationId = 0)
{
    /// <summary>
    /// What is left. ⚠️ Can go NEGATIVE, and that is deliberate — see <see cref="IsOversubscribed"/>.
    /// </summary>
    public int Remaining => Purchased - Claimed;

    /// <summary>
    /// 🔴 More has been claimed than was bought. The partner owes for the excess, or somebody has
    /// been given a ticket nobody paid for.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is why <see cref="Remaining"/> is not clamped at zero.</b> Clamping would hide the
    /// one state that actually costs money — a pool showing "0 left" reads as "fully used, fine",
    /// where "-3" reads as "three tickets nobody has paid for". The number has to be able to tell
    /// the truth.
    /// </remarks>
    public bool IsOversubscribed => Remaining < 0;

    /// <summary>True when the pool is at or below its warning level (including negative).</summary>
    /// <remarks>
    /// §795.1 — a pool CLOSED BY HAND is never "low": nobody is going to claim against it, so
    /// warning about it is noise about a decision somebody already made (§302).
    /// </remarks>
    public bool IsLow => !ClosedByHand && Remaining <= LowBalanceThreshold;

    /// <summary>True when the pool is exactly used up — no more, but none over.</summary>
    public bool IsExhausted => Remaining == 0;

    /// <summary>
    /// §795.1 — open, used in full, or closed by hand.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>The human decision WINS over the arithmetic</b>, and the order of these two lines is the
    /// whole requirement: a pool somebody ended stays closed even when a cancellation puts tickets
    /// back in it, where a pool that merely ran out re-opens the moment one comes back.
    /// </remarks>
    public CouponPoolState State =>
        ClosedByHand ? CouponPoolState.ClosedByHand
        : Remaining <= 0 ? CouponPoolState.UsedInFull
        : CouponPoolState.Open;

    /// <summary>True when the pool is closed for either reason — nothing more should be claimed.</summary>
    public bool IsClosed => State != CouponPoolState.Open;

    /// <summary>
    /// §795.2/§798.4 — true once EVERY purchase on this pool carries an e-conomic invoice number.
    /// </summary>
    /// <remarks>
    /// 🔴 False means <b>CEH has no evidence the partner was charged for part of what they hold</b> —
    /// not "the invoice is late". A prepaid pool is never invoiced by CEH, so nothing else in the
    /// system would notice. ⚠️ It is <c>== 0</c> and not a flag on the pool precisely because a pool
    /// topped up after being invoiced once would otherwise read as fully billed.
    /// </remarks>
    public bool IsBilled => UnbilledPurchases == 0;

    /// <summary>How a person reads the state on the page.</summary>
    public string StateLabel => State switch
    {
        CouponPoolState.ClosedByHand => "closed",
        CouponPoolState.UsedInFull => IsOversubscribed ? "oversubscribed" : "used in full",
        _ => "open",
    };
}

/// <summary>
/// §794 — computes prepaid pool balances. Pure, so the arithmetic that tells a partner how many
/// tickets they have left can be tested without a database.
/// </summary>
public static class CouponPrepaidBalance
{
    /// <summary>Default warning level when an allocation does not set its own.</summary>
    public const int DefaultLowBalanceThreshold = 5;

    /// <summary>
    /// Balance one allocation against the claims on its coupon.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>DERIVED, never accumulated.</b> The claim set is the CURRENT state of the order
    /// mirror, so a cancellation returns its allocation by simply no longer appearing — no release
    /// event and nothing to replay. Re-running this a hundred times gives the same answer, which a
    /// running total could not promise.</para>
    ///
    /// <para>⚠️ Claims are matched on <c>TicketClassId</c>, never on the class name (§787.16).</para>
    /// </remarks>
    /// <param name="purchases">
    /// §798.4 — the agreed purchases on this pool. Null falls back to
    /// <see cref="CouponPrepaidAllocation.Purchases"/>, so a caller that loaded them through the
    /// navigation does not have to pass them twice.
    /// </param>
    public static CouponPoolBalance For(
        CouponPrepaidAllocation allocation,
        IEnumerable<CouponClaim> claimsForThisCoupon,
        IEnumerable<CouponPrepaidPurchase>? purchases = null)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(claimsForThisCoupon);

        var claimed = claimsForThisCoupon.Count(c =>
            !c.IsCancelled
            && string.Equals(c.TicketClassId, allocation.TicketClassId, StringComparison.Ordinal));

        // §798.4 — SUMMED, never stored. Topping up adds a row; nothing is edited, so nothing drifts.
        var bought = (purchases ?? allocation.Purchases ?? Array.Empty<CouponPrepaidPurchase>())
            .ToList();

        return new CouponPoolBalance(
            allocation.TicketClassId,
            allocation.TicketClassLabel,
            bought.Sum(p => p.Quantity),
            claimed,
            allocation.LowBalanceThreshold ?? DefaultLowBalanceThreshold,
            // §795.1 — only the HUMAN close travels with the balance. "Used in full" is derived
            // from the arithmetic above, every time, so it cannot go stale.
            ClosedByHand: allocation.ClosedAt is not null,
            // §795.2/§798.4 — per PURCHASE, so a topped-up pool cannot read as fully billed.
            UnbilledPurchases: bought.Count(p => !p.IsBilled),
            AllocationId: allocation.Id);
    }

    /// <summary>Every pool on one coupon, in the order a person would read them.</summary>
    public static IReadOnlyList<CouponPoolBalance> ForCoupon(
        IEnumerable<CouponPrepaidAllocation> allocations,
        IEnumerable<CouponClaim> claimsForThisCoupon)
    {
        var claims = claimsForThisCoupon as IReadOnlyList<CouponClaim> ?? claimsForThisCoupon.ToList();
        return allocations
            .Select(a => For(a, claims))
            // Trouble first: oversubscribed, then low, then the healthy ones.
            .OrderBy(b => b.Remaining)
            .ThenBy(b => b.Label ?? b.TicketClassId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
