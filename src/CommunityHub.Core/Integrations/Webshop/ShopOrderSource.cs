using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// §1305 item 7b — presents SIGNED CEH shop orders in the same shape as completed webshop orders, so the
/// one existing onboarding pipeline handles both.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-26: <i>"once a sponsor signs, then the normal flow to onboard them as sponsor
/// starts"</i>. The WooCommerce pull (<c>SponsorOrderPullService</c>) turns completed orders into tasks,
/// package/booth flags, contacts, the welcome mail and Zoho provisioning. Rather than a second copy of
/// that pipeline, a signed CEH order is fed into it as one more order.</para>
///
/// <para>🔑 <b>The order id is NEGATIVE</b> (<c>-ShopOrder.Id</c>) so it can never collide with a
/// webshop order id. The pipeline uses the id only for the earliest-order date; the webshop invoice job
/// never sees these (it reads the webshop, not this source).</para>
///
/// <para>🔑 <b>Categories are the webshop's category NAMES</b>, joined like the webshop's own line items,
/// because the product classifier matches on them.</para>
/// </remarks>
public sealed class ShopOrderSource
{
    private readonly CommunityHubDbContext _db;

    public ShopOrderSource(CommunityHubDbContext db) => _db = db;

    /// <summary>Every signed CEH order for the edition, as webshop-shaped orders.</summary>
    public async Task<IReadOnlyList<WooOrder>> SignedOrdersAsync(int eventId, CancellationToken ct = default)
    {
        var orders = await _db.ShopOrders.AsNoTracking()
            .Where(o => o.EventId == eventId && o.Status == ShopOrderStatus.Signed)
            .Include(o => o.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.Categories).ThenInclude(c => c.Category)
            .ToListAsync(ct);

        var companies = await _db.ShopCompanies.AsNoTracking()
            .Where(c => c.EventId == eventId)
            .ToDictionaryAsync(c => c.SponsorCompanyId, c => c, ct);

        return orders.Select(o => ToWooOrder(o, companies.GetValueOrDefault(o.SponsorCompanyId))).ToList();
    }

    /// <summary>Signed CEH orders of the ACTIVE edition — for callers that are not edition-scoped.</summary>
    public async Task<IReadOnlyList<WooOrder>> SignedOrdersForActiveEventAsync(CancellationToken ct = default)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        return eventId is int id ? await SignedOrdersAsync(id, ct) : Array.Empty<WooOrder>();
    }

    /// <summary>Shape one signed CEH order like a completed webshop order.</summary>
    public static WooOrder ToWooOrder(ShopOrder o, ShopCompany? company)
    {
        var lines = o.Lines.Select(l =>
        {
            var cats = string.Join(", ", (l.Product?.Categories ?? new())
                .Select(c => c.Category?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n)));
            var unit = Math.Round(l.UnitPriceEur * (1 + l.SurchargePercent / 100m), 2, MidpointRounding.AwayFromZero);
            return new WooLineItem(
                ProductId: l.Product?.WooProductId ?? -l.ProductId,
                ProductName: l.ProductName,
                CategoriesText: cats,
                Quantity: l.Quantity,
                UnitPrice: unit,
                LineSubtotal: unit * l.Quantity,
                LineTotal: unit * l.Quantity);
        }).ToList();

        return new WooOrder(
            OrderId: -o.Id,
            Status: "completed",
            BillingEmail: company?.InvoiceEmail ?? company?.CompanyEmail ?? o.SignedByEmail ?? string.Empty,
            BillingCompany: company?.LegalName ?? string.Empty,
            CompanyId: o.SponsorCompanyId,
            CreatedAt: o.SignedAt ?? o.CreatedAt,
            LineItems: lines);
    }
}
