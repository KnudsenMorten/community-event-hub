using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1017 — webshop orders carrying a COUPON must invoice correctly AND say why.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: *"my invoice solution from cm weborders, must support coupons +
/// discounts … it is in the order api, can you validate and extend the erp invoicing to support
/// that"*.</para>
///
/// <para>✅ <b>THE VALIDATION CAME BACK: THE AMOUNT WAS NEVER WRONG.</b> Read live from the
/// WooCommerce API, 2026-08-09:</para>
/// <code>
/// order 10841  discount_total=600  coupon_lines=[free3extratickets disc=600]
///   line "Pre-day Sponsor ELDK27"  qty=1  price=24400  subtotal=25000  total=24400
/// order 10831  (no coupon)
///   line "Discounted Tickets…"     qty=3  price=200    subtotal=600    total=600
/// </code>
/// <para>So WooCommerce's <c>price</c> is the <b>post-discount unit price</b> (total ÷ quantity),
/// and CEH — which bills <c>price × quantity</c> — has been charging the discounted figure all
/// along.</para>
///
/// <para>🔑 <b>What was actually missing is the EXPLANATION.</b> The sponsor got an invoice for
/// 24 400 against a product they know costs 25 000, with neither the coupon code nor the discount
/// printed anywhere. That is a number a customer cannot reconcile, which means a phone call.</para>
/// </remarks>
public sealed class WebshopCouponDiscountTests
{
    /// <summary>The live order 10841 line, as the API returned it.</summary>
    private static WebshopOrderLine DiscountedLine() =>
        new("Pre-day Sponsor ELDK27", 1m, 24400m,
            LineSubtotalEur: 25000m, LineTotalEur: 24400m,
            CouponCodes: new[] { "free3extratickets" });

    [Fact]
    public void A_discounted_line_prints_the_coupon_and_the_reduction()
    {
        var lines = WebshopInvoiceLineComposer.Compose(
            "10841", new DateTimeOffset(2026, 8, 9, 18, 56, 0, TimeSpan.Zero),
            new[] { DiscountedLine() }, vatZoneNumber: 2, convert: p => (p, null));

        var product = lines.Single(l => l.UnitNetPrice is not null);

        Assert.Contains("Pre-day Sponsor ELDK27", product.Description);
        Assert.Contains("free3extratickets", product.Description);      // WHICH coupon
        Assert.Contains("25000.00 EUR", product.Description);           // the list price
        Assert.Contains("600.00 EUR discount", product.Description);    // and the reduction

        // 🔒 THE AMOUNT IS UNCHANGED — it was already the discounted one. A "fix" that subtracted
        // the discount again would bill this sponsor 23 800 for a 24 400 order.
        Assert.Equal(24400m, product.UnitNetPrice);
        Assert.Equal(1m, product.Quantity);
    }

    [Fact]
    public void An_undiscounted_line_is_completely_unchanged()
    {
        // Order 10831's third line: qty 3 × 200 = 600, no coupon anywhere. Every invoice CEH has
        // ever sent looks like this, so it must read exactly as it did before §1017.
        var lines = WebshopInvoiceLineComposer.Compose(
            "10831", null,
            new[] { new WebshopOrderLine("Discounted Tickets for Extra Exhibitor Staff ELDK27", 3m, 200m,
                        LineSubtotalEur: 600m, LineTotalEur: 600m, CouponCodes: null) },
            vatZoneNumber: 2, convert: p => (p, null));

        var product = lines.Single(l => l.UnitNetPrice is not null);

        // ⚠️ Asserted as EQUALITY, not as "does not contain 'discount'" — this real product is
        // literally called "Discounted Tickets…", and a substring check on that word passes or
        // fails on the product NAME rather than on anything this change does.
        Assert.Equal("Discounted Tickets for Extra Exhibitor Staff ELDK27", product.Description);
        Assert.Equal(200m, product.UnitNetPrice);
    }

    /// <summary>
    /// 🔒 Every construction site that predates §1017 supplies no subtotal/total, and must keep
    /// printing exactly what it printed before — silence, not a "0.00 discount" line.
    /// </summary>
    [Fact]
    public void A_line_with_no_totals_supplied_prints_no_discount_note()
    {
        var lines = WebshopInvoiceLineComposer.Compose(
            "10000", null, new[] { new WebshopOrderLine("Booth package", 1m, 7500m) },
            vatZoneNumber: 1, convert: p => (p, null));

        Assert.Equal("Booth package", lines.Single(l => l.UnitNetPrice is not null).Description);
    }

    [Fact]
    public void The_discount_note_sits_alongside_a_currency_conversion_note()
    {
        // Both notes can apply at once; neither may swallow the other, and the product name stays
        // first because that is what the reader scans for.
        var lines = WebshopInvoiceLineComposer.Compose(
            "10841", null, new[] { DiscountedLine() },
            vatZoneNumber: 2, convert: p => (p * 7.46m, "Currency conversion applied:\r\nEUR -> DKK"));

        var d = lines.Single(l => l.UnitNetPrice is not null).Description;

        Assert.StartsWith("Pre-day Sponsor ELDK27", d);
        Assert.Contains("free3extratickets", d);
        Assert.Contains("Currency conversion applied:", d);
    }

    [Theory]
    // subtotal == total ⇒ nothing was discounted
    [InlineData(600, 600, false)]
    // 🔒 A total ABOVE the subtotal is not a discount — it is a fee or bad data, and inventing a
    // negative "discount" for it would print nonsense on a customer's invoice.
    [InlineData(600, 700, false)]
    [InlineData(0, 0, false)]
    [InlineData(25000, 24400, true)]
    public void Only_a_real_reduction_counts_as_a_discount(decimal subtotal, decimal total, bool expected)
    {
        var note = WebshopInvoiceLineComposer.ComposeDiscountNote(subtotal, total, "EUR", new[] { "X" });
        Assert.Equal(expected, note is not null);

        var li = new WooLineItem(1, "P", "", 1, total, subtotal, total);
        Assert.Equal(expected, li.IsDiscounted);
    }

    [Fact]
    public void A_discount_with_no_coupon_code_still_explains_itself()
    {
        // A manual order-level reduction has no coupon_lines entry, but the sponsor still sees a
        // number below list price and still deserves the reason.
        var note = WebshopInvoiceLineComposer.ComposeDiscountNote(25000m, 24400m, "EUR", null);

        Assert.NotNull(note);
        Assert.StartsWith("Discount:", note);
        Assert.Contains("600.00 EUR discount", note);
    }
}
