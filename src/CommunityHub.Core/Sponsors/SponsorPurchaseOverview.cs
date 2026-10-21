using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Webshop;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Sponsors;

/// <summary>
/// §1359 — per sponsor company: the booth tier, the sponsor categories and every product bought, for the organizer
/// Sponsors page and its Excel download. Operator 2026-10-19: *"their tier, their extra products, their sponsor category,
/// etc where can i find that"* · *"my event partner sabine needs it"*.
/// </summary>
/// <remarks>
/// 🔒 Only REAL purchases: completed webshop orders and signed CEH shop orders — the same orders the sponsor-order pull
/// trusts. A cancelled order never appears. The tier shows only while a current booth order backs it
/// (<c>HasCurrentBoothOrder</c>, §1163/§1348): <c>SponsorInfo.Tier</c> is raise-only and survives a cancellation.
/// </remarks>
public static class SponsorPurchaseOverview
{
    /// <summary>One company's purchases. <see cref="Tier"/> is null without a current booth order.</summary>
    public sealed record Summary(string? Tier, IReadOnlyList<string> Categories, IReadOnlyList<string> Products)
    {
        public static readonly Summary Empty = new(null, Array.Empty<string>(), Array.Empty<string>());
    }

    public static async Task<IReadOnlyDictionary<string, Summary>> BuildAsync(
        CommunityHubDbContext db, int eventId, CancellationToken ct = default)
    {
        var infos = await db.SponsorInfos.IgnoreQueryFilters()
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.SponsorCompanyId, s.Tier, s.HasCurrentBoothOrder })
            .ToListAsync(ct);

        var orders = (await new WebshopOrderArchiveSource(db).GetOrdersAsync("completed", ct))
            .Concat(await new ShopOrderSource(db).SignedOrdersAsync(eventId, ct))
            .Where(o => !string.IsNullOrWhiteSpace(o.CompanyId))
            .ToList();

        var products = orders
            .GroupBy(o => o.CompanyId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.SelectMany(o => o.LineItems)
                    .Where(l => !string.IsNullOrWhiteSpace(l.ProductName))
                    .GroupBy(l => l.ProductName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(p => (Name: p.Key, Qty: p.Sum(l => Math.Max(1, l.Quantity))))
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => p.Qty > 1 ? $"{p.Qty} × {p.Name}" : p.Name)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        // Categories from the CURRENT order lines (the shop's product categories, e.g. "Sponsor Sessions"), not the
        // stored Zoho set — that one is raise-only and keeps a cancelled product's category for ever.
        var categories = orders
            .GroupBy(o => o.CompanyId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.SelectMany(o => o.LineItems)
                    .SelectMany(l => SplitCategories(l.CategoriesText))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, Summary>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in infos.GroupBy(i => i.SponsorCompanyId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            result[i.SponsorCompanyId] = new Summary(
                i.HasCurrentBoothOrder && i.Tier != BoothTier.None ? i.Tier.ToString() : null,
                categories.GetValueOrDefault(i.SponsorCompanyId) ?? Array.Empty<string>(),
                products.GetValueOrDefault(i.SponsorCompanyId) ?? Array.Empty<string>());
        }
        foreach (var (companyId, list) in products)
            result.TryAdd(companyId, new Summary(null, categories.GetValueOrDefault(companyId) ?? Array.Empty<string>(), list));
        return result;
    }

    /// <summary>A line's category text ("Sponsor Sessions, Exhibitor") as separate names.</summary>
    public static IEnumerable<string> SplitCategories(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? Enumerable.Empty<string>()
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
