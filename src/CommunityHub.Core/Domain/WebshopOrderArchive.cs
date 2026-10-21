namespace CommunityHub.Core.Domain;

/// <summary>
/// 🔴 §1312 — one order from the OLD webshop (WooCommerce), stored in CEH so nothing has to ask WooCommerce again (operator
/// 2026-10-01: "i prefer that you store the information in ceh about the orders/products that was bought using the old
/// webshop, so we dont need to lookup against woocommerce" · "old webshop is closed for new orders").
/// </summary>
/// <remarks>
/// <para>🔒 <b>Read-only history, kept apart from <see cref="ShopOrder"/></b> on purpose: these orders were invoiced in e-conomic as
/// <c>WebshopOrderId-{OrderId}</c>, took their stock in the old shop and were never signed in CEH, so nothing in the CEH order
/// lifecycle (invoicing, stock, contracts, cancel, reminders, pricing) may ever act on them.</para>
/// <para>🔑 <see cref="Json"/> is the order exactly as every reader already consumes it (<see cref="Integrations.WooOrder"/>:
/// the webshop order and product ids, company id, dates, product names, the CATEGORY NAMES captured at import — they existed
/// only in the webshop — quantities, paid prices and coupons), so each reader keeps its logic unchanged.</para>
/// </remarks>
public class WebshopOrderArchive
{
    /// <summary>The old webshop's order id — the key of its e-conomic invoice reference <c>WebshopOrderId-{id}</c>.</summary>
    public long OrderId { get; set; }

    /// <summary>The edition the order was imported for (the webshop's order window of that edition).</summary>
    public int EventId { get; set; }

    /// <summary>completed / processing / cancelled / refunded / … as the webshop had it.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The company id (<see cref="ShopCompany.SponsorCompanyId"/>), or null for an order without one.</summary>
    public string? CompanyId { get; set; }

    public string BillingCompany { get; set; } = string.Empty;
    public string BillingEmail { get; set; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>The whole order as <see cref="Integrations.WooOrder"/> JSON.</summary>
    public string Json { get; set; } = string.Empty;

    public DateTimeOffset ArchivedAt { get; set; }
}
