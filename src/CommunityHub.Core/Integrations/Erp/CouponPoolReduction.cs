using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §1362 — reduce a prepaid pool: a CORRECTION purchase with a negative quantity, backed by a credit note raised in
/// e-conomic by hand. The pool stays derived (purchased = SUM of purchases, §798.4), the purchase history and its
/// invoice numbers are kept, and nothing is invoiced.
/// </summary>
/// <remarks>
/// Operator 2026-10-20: *"i need to reduce coupons for apento by -9 as it was a mistake … i will prepare creditnote in
/// erp, but can i reduce the coupons available somewhere"*. Before this a pool could only grow (§992) or be removed
/// outright, which deletes its purchase history and invoice numbers too.
/// </remarks>
public static class CouponPoolReduction
{
    /// <summary>The correction to record (a negative quantity), or why the new total is refused.</summary>
    public sealed record Plan(int Delta, string? Refusal)
    {
        public bool Ok => Refusal is null;
    }

    /// <summary>
    /// Plan a reduction of a pool that covers <paramref name="purchased"/> tickets, of which <paramref name="claimed"/>
    /// are held by attendees, to <paramref name="newTotal"/>.
    /// </summary>
    public static Plan For(int purchased, int claimed, int newTotal)
    {
        if (newTotal < 0)
            return new Plan(0, "Enter the new total the code should allow (0 or more).");
        if (newTotal >= purchased)
            return new Plan(0, $"This pool covers {purchased} ticket(s). Enter a LOWER new total to reduce it "
                               + "— use 'Increase to' to raise it.");
        // 🔒 Never below what attendees already hold: those tickets exist, and a pool that says otherwise would read as
        // tickets nobody paid for.
        if (newTotal < claimed)
            return new Plan(0, $"{claimed} ticket(s) are already claimed on this pool, so it cannot go below {claimed}.");
        return new Plan(newTotal - purchased, null);
    }

    /// <summary>
    /// The unit price a correction is valued at: the latest priced purchase on the pool, so the pool's agreed value nets
    /// down by what the credit note gives back. Null when no purchase carries a price.
    /// </summary>
    public static decimal? UnitPriceFor(IEnumerable<CouponPrepaidPurchase> purchases) =>
        purchases
            .Where(p => p.Quantity > 0 && p.UnitPriceDkk is > 0m)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Select(p => p.UnitPriceDkk)
            .FirstOrDefault();

    /// <summary>A correction (reduction) rather than a purchase: its number is a credit note, not an invoice.</summary>
    public static bool IsCorrection(CouponPrepaidPurchase purchase) => purchase.Quantity < 0;
}
