using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §794 — the prepaid pool arithmetic: bought, used, left.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"lets say arrow dk buys 50 prepaid coupons. we need to deduct when
/// claimed so they know remaining"* … *"pool is linked to a ticket class 2 day"* … *"partner must not
/// get a credit note"*.</para>
///
/// <para>🔒 This number goes to a PARTNER, and it is the number they plan against. It is derived from
/// the current state of the order mirror rather than accumulated, which is what these tests exist to
/// hold in place.</para>
/// </remarks>
public sealed class CouponPrepaidBalanceTests
{
    private const string TwoDay = "14880000003485482";
    private const string OneDay = "14880000003485481";

    /// <summary>
    /// §798.4 — the quantity now lives on the PURCHASES, so a pool of 50 is a pool with one 50-ticket
    /// purchase. <paramref name="topUps"/> adds further agreed buys under the same coupon code.
    /// </summary>
    private static CouponPrepaidAllocation Pool(
        string classId = TwoDay, int purchased = 50, int? threshold = null,
        params int[] topUps) =>
        new()
        {
            EventId = 1,
            CouponInvoicingSettingId = 1,
            TicketClassId = classId,
            TicketClassLabel = classId == TwoDay ? "2-day" : "1-day",
            LowBalanceThreshold = threshold,
            Purchases = new[] { purchased }.Concat(topUps)
                .Select(q => new CouponPrepaidPurchase { Quantity = q, ErpInvoiceNumber = "20147" })
                .ToList(),
        };

    private static CouponClaim Claim(string classId = TwoDay, bool cancelled = false, string id = "t") =>
        new(
            Reference: $"ARROW-DK-{id}",
            CouponName: "ARROW-DK",
            OrderId: "9001",
            TicketId: id,
            Email: $"{id}@example.test",
            FirstName: "A",
            LastName: "Attendee",
            TicketClassName: classId == TwoDay ? "2-day" : "1-day",
            UnitPriceDkk: classId == TwoDay ? 3500m : 2000m,
            TicketClassId: classId,
            IsCancelled: cancelled);

    /// <summary>Arrow DK buys 50, three are claimed, 47 remain. The whole feature in one line.</summary>
    [Fact]
    public void Claims_deduct_from_the_pool()
    {
        var b = CouponPrepaidBalance.For(Pool(purchased: 50), new[]
        {
            Claim(id: "1"), Claim(id: "2"), Claim(id: "3"),
        });

        Assert.Equal(50, b.Purchased);
        Assert.Equal(3, b.Claimed);
        Assert.Equal(47, b.Remaining);
        Assert.False(b.IsLow);
        Assert.False(b.IsExhausted);
        Assert.False(b.IsOversubscribed);
    }

    /// <summary>
    /// 🔴 A CANCELLED claim RELEASES its allocation — by simply not counting.
    /// </summary>
    /// <remarks>
    /// This is what makes *"partner must not get a credit note"* satisfiable. Nothing is refunded and
    /// no compensating entry is written: the ticket stops being a live claim, so the pool returns to
    /// what it was. That only works because the balance is DERIVED — a running total would have
    /// needed a release event, and a missed or repeated run would then drift for ever.
    /// </remarks>
    [Fact]
    public void A_cancelled_claim_returns_to_the_pool()
    {
        var claims = new[] { Claim(id: "1"), Claim(id: "2", cancelled: true), Claim(id: "3") };
        var b = CouponPrepaidBalance.For(Pool(purchased: 10), claims);

        Assert.Equal(2, b.Claimed);      // the cancelled one is not spent
        Assert.Equal(8, b.Remaining);
    }

    /// <summary>
    /// 🔒 Re-deriving gives the same answer, however often it runs. A running total could not
    /// promise this, and the drift would be silent.
    /// </summary>
    [Fact]
    public void Deriving_it_repeatedly_never_drifts()
    {
        var pool = Pool(purchased: 20);
        var claims = new[] { Claim(id: "1"), Claim(id: "2"), Claim(id: "3", cancelled: true) };

        var first = CouponPrepaidBalance.For(pool, claims);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(first.Remaining, CouponPrepaidBalance.For(pool, claims).Remaining);
        }
        Assert.Equal(18, first.Remaining);
    }

    /// <summary>
    /// 🔴 POOLS ARE PER TICKET CLASS AND SPENT INDEPENDENTLY — a 2-day claim must never eat the
    /// 1-day pool (operator: *"pool is linked to a ticket class 2 day"*).
    /// </summary>
    [Fact]
    public void A_claim_only_spends_its_own_ticket_class()
    {
        var claims = new[]
        {
            Claim(TwoDay, id: "1"), Claim(TwoDay, id: "2"),
            Claim(OneDay, id: "3"),
        };

        Assert.Equal(2, CouponPrepaidBalance.For(Pool(TwoDay, 10), claims).Claimed);
        Assert.Equal(1, CouponPrepaidBalance.For(Pool(OneDay, 10), claims).Claimed);
    }

    /// <summary>
    /// 🔒 Matched on the class ID, never the NAME. A class renamed in Backstage must not move a
    /// partner's allocation to a different pool (§787.16).
    /// </summary>
    [Fact]
    public void Matching_is_by_class_id_not_by_name()
    {
        var renamed = Claim(TwoDay, id: "1") with { TicketClassName = "Two Day Conference Pass 2027" };
        Assert.Equal(1, CouponPrepaidBalance.For(Pool(TwoDay, 10), new[] { renamed }).Claimed);
    }

    /// <summary>
    /// 🔴 REMAINING IS NOT CLAMPED AT ZERO, and that is the point: "-3" says three tickets nobody
    /// paid for, where "0" would read as "fully used, fine".
    /// </summary>
    [Fact]
    public void An_oversubscribed_pool_goes_negative_rather_than_hiding_it()
    {
        var claims = Enumerable.Range(1, 5).Select(i => Claim(id: $"t{i}")).ToArray();
        var b = CouponPrepaidBalance.For(Pool(purchased: 2), claims);

        Assert.Equal(-3, b.Remaining);
        Assert.True(b.IsOversubscribed);
        Assert.True(b.IsLow);
        Assert.False(b.IsExhausted);     // exhausted means exactly 0, not "past it"
    }

    [Fact]
    public void An_exactly_used_pool_is_exhausted_but_not_oversubscribed()
    {
        var claims = Enumerable.Range(1, 4).Select(i => Claim(id: $"t{i}")).ToArray();
        var b = CouponPrepaidBalance.For(Pool(purchased: 4), claims);

        Assert.Equal(0, b.Remaining);
        Assert.True(b.IsExhausted);
        Assert.False(b.IsOversubscribed);
        Assert.True(b.IsLow);
    }

    [Fact]
    public void The_low_warning_uses_the_pools_own_threshold_when_set()
    {
        var claims = Enumerable.Range(1, 8).Select(i => Claim(id: $"t{i}")).ToArray();

        // 10 bought, 8 used ⇒ 2 left.
        Assert.True(CouponPrepaidBalance.For(Pool(purchased: 10, threshold: 3), claims).IsLow);
        Assert.False(CouponPrepaidBalance.For(Pool(purchased: 10, threshold: 1), claims).IsLow);

        // Default applies when the allocation sets none.
        Assert.Equal(
            CouponPrepaidBalance.DefaultLowBalanceThreshold,
            CouponPrepaidBalance.For(Pool(purchased: 10), claims).LowBalanceThreshold);
    }

    /// <summary>
    /// 🔴 §798.4 — A POOL IS THE SUM OF ITS PURCHASES. "50 in January, 25 more in March" is 75
    /// available under the same coupon code, with two invoices behind it.
    /// </summary>
    [Fact]
    public void A_topped_up_pool_is_the_sum_of_every_purchase()
    {
        var pool = Pool(purchased: 50, topUps: new[] { 25 });
        var claims = Enumerable.Range(1, 60).Select(i => Claim(id: $"t{i}")).ToArray();

        var b = CouponPrepaidBalance.For(pool, claims);

        Assert.Equal(75, b.Purchased);
        Assert.Equal(60, b.Claimed);
        Assert.Equal(15, b.Remaining);          // and it is NOT oversubscribed at 60 of 75
        Assert.False(b.IsOversubscribed);
    }

    /// <summary>
    /// 🔴 §798.4 — "billed" is per PURCHASE. A pool whose first 50 were invoiced in January and
    /// whose 25-ticket top-up has no number is NOT billed, and one flag on the pool would have said
    /// it was.
    /// </summary>
    [Fact]
    public void A_top_up_with_no_invoice_number_leaves_the_pool_unbilled()
    {
        var pool = Pool(purchased: 50);
        pool.Purchases.Add(new CouponPrepaidPurchase { Quantity = 25 });   // no invoice number

        var b = CouponPrepaidBalance.For(pool, Array.Empty<CouponClaim>());

        Assert.Equal(75, b.Purchased);
        Assert.Equal(1, b.UnbilledPurchases);
        Assert.False(b.IsBilled);
    }

    /// <summary>Trouble first — an organizer scanning the page sees the problem pools at the top.</summary>
    [Fact]
    public void Pools_are_ordered_worst_first()
    {
        var pools = new[] { Pool(OneDay, 100), Pool(TwoDay, 2) };
        var claims = Enumerable.Range(1, 5).Select(i => Claim(TwoDay, id: $"t{i}")).ToArray();

        var ordered = CouponPrepaidBalance.ForCoupon(pools, claims);

        Assert.Equal(TwoDay, ordered[0].TicketClassId);   // -3, the one that costs money
        Assert.True(ordered[0].IsOversubscribed);
        Assert.Equal(OneDay, ordered[1].TicketClassId);
    }
}
