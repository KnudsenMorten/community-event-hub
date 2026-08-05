using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §807.1 — a zero-priced product must still produce an invoice, at 0.00.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"even though product prices are 0, we need an invoice of 0. this is
/// for documentation and is relevant to show sponsors if they receive a product as discount"*.</para>
///
/// <para>🔴 <b>This REVERSES §786's refusal</b>, which returned *"Invoicing a 0.00 row would reach the
/// customer as a wrong invoice"* and left the order uninvoiced. A zero-priced product is a discount
/// the sponsor received, and the 0.00 invoice is the only record that says so — refusing it left the
/// transaction with no trace at all.</para>
///
/// <para>🔒 A NEGATIVE line is still refused: that is a credit note, and nothing here raises one.</para>
/// </remarks>
public sealed class ZeroPriceOrderStillInvoicedTests
{
    private static WebshopOrderLine Line(string name, decimal price, decimal qty = 1m) =>
        new(name, qty, price);

    /// <summary>The composed invoice keeps the 0.00 line rather than dropping it.</summary>
    [Fact]
    public void A_zero_priced_line_is_composed_onto_the_invoice()
    {
        var composed = WebshopInvoiceLineComposer.Compose(
            orderNumber: "10812",
            orderDate: new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero),
            orderLines: new[] { Line("Sponsor discount product", 0m) },
            vatZoneNumber: 1,
            convert: eur => (eur, null));

        // The header line carries no price; the product line does — at zero.
        var priced = composed.Where(l => l.UnitNetPrice is not null).ToList();
        var zero = Assert.Single(priced);
        Assert.Equal(0m, zero.UnitNetPrice);
        Assert.Contains("Sponsor discount product", zero.Description);
    }

    /// <summary>
    /// 🔑 A mixed order — one paid product, one free — invoices BOTH. The free one is the line that
    /// documents the discount, which is the whole point.
    /// </summary>
    [Fact]
    public void A_free_product_alongside_a_paid_one_is_still_listed()
    {
        var composed = WebshopInvoiceLineComposer.Compose(
            orderNumber: "10812",
            orderDate: new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero),
            orderLines: new[] { Line("Gold sponsorship", 5000m), Line("Extra booth banner (free)", 0m) },
            vatZoneNumber: 1,
            convert: eur => (eur, null));

        var priced = composed.Where(l => l.UnitNetPrice is not null).ToList();
        Assert.Equal(2, priced.Count);
        Assert.Contains(priced, l => l.UnitNetPrice == 5000m);
        Assert.Contains(priced, l => l.UnitNetPrice == 0m);
    }

    /// <summary>
    /// §807.2 — the currency conversion applies to a zero line without inventing anything: 0 × any
    /// rate is 0, and the invoice still says which currency it is in.
    /// </summary>
    [Fact]
    public void Converting_a_zero_line_stays_zero()
    {
        var composed = WebshopInvoiceLineComposer.Compose(
            orderNumber: "10812",
            orderDate: new DateTimeOffset(2026, 8, 4, 9, 0, 0, TimeSpan.Zero),
            orderLines: new[] { Line("Free of charge item", 0m) },
            vatZoneNumber: 1,
            // the real EUR→DKK rate measured against the live API on 2026-08-04
            convert: eur => (Math.Ceiling(eur * 7.46m), "EUR→DKK @ 7.46"));

        var zero = Assert.Single(composed.Where(l => l.UnitNetPrice is not null));
        Assert.Equal(0m, zero.UnitNetPrice);
    }
}
