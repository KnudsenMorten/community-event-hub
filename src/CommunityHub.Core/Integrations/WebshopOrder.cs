namespace CommunityHub.Core.Integrations;

// §1312 — the old webshop's order shape, kept as the stored form of its orders (WebshopOrderArchive) after the webshop
// client itself was removed (the webshop is closed). Every reader of old orders consumes these records.

/// <summary>A WooCommerce order line item, as the hub needs it.</summary>
/// <param name="Quantity">
/// §666 — HOW MANY of this product the line carries. The operator asked for the amount, not just
/// yes/no: <i>"did he buy TV (amount) or did he not"</i>, and a sponsor may have bought two on one
/// line. Defaulted to 1 so every existing construction site keeps its meaning (one line ⇒ one item)
/// rather than silently becoming zero.
/// </param>
/// <param name="UnitPrice">
/// §786 — the per-unit price WooCommerce charged, in the WEBSHOP's currency (EUR). The hub now
/// invoices these orders, and the retired PowerShell script read exactly this field.
/// <para>🔒 Defaulted to 0 so every existing construction site is unchanged. The sponsor task
/// pipeline reads this record for <i>"did he buy it"</i>, never for money, and must not start
/// depending on a price it never asked for. A zero therefore means NOT SUPPLIED — and the invoicing
/// service refuses such a line rather than sending a customer a 0.00 row.</para>
/// </param>
/// <param name="LineSubtotal">
/// §1017 — the line total BEFORE any coupon, i.e. list price × quantity. Compared against
/// <paramref name="LineTotal"/> to know whether this line was discounted at all.
/// </param>
/// <param name="LineTotal">
/// §1017 — the line total AFTER coupons. ✅ Live-verified on order 10841: subtotal 25000,
/// total 24400 for the `free3extratickets` coupon.
/// <para>🔒 <b>This, not <see cref="UnitPrice"/> × quantity, is the rounding-safe basis for an
/// amount.</b> WooCommerce derives `price` as total/quantity, so a discount that does not divide
/// evenly across a multi-unit line loses cents when re-multiplied.</para>
/// </param>
public sealed record WooLineItem(
    long ProductId,
    string ProductName,
    string CategoriesText,
    int Quantity = 1,
    decimal UnitPrice = 0m,
    decimal LineSubtotal = 0m,
    decimal LineTotal = 0m)
{
    /// <summary>§1017 — how much this line was reduced by coupons (0 when it was not).</summary>
    public decimal DiscountAmount =>
        LineSubtotal > 0m && LineTotal > 0m && LineSubtotal > LineTotal
            ? LineSubtotal - LineTotal
            : 0m;

    /// <summary>§1017 — true when a coupon actually reduced this line.</summary>
    public bool IsDiscounted => DiscountAmount > 0m;
}

/// <summary>A WooCommerce order, flattened to what the sponsor pipeline uses.</summary>
public sealed record WooOrder(
    long OrderId,
    string Status,
    string BillingEmail,
    string BillingCompany,
    string? CompanyId,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<WooLineItem> LineItems,
    /// <summary>
    /// §1017 — the coupon codes applied to this order. WooCommerce reports coupons per ORDER while
    /// the reduction lands per LINE, so this is the only place the CODE exists — and the code is
    /// the thing a sponsor asks about when the total is not the list price.
    /// </summary>
    IReadOnlyList<string>? CouponCodes = null);
