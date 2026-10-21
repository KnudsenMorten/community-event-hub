using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>§1305 item 11 — the state of one booth on the expo map.</summary>
public enum ShopBoothState
{
    /// <summary>A spot with no product for sale (not for sale this edition, or not yet set up).</summary>
    NotForSale = 0,
    Free = 1,
    /// <summary>Held by a CEH order that is reserved / waiting for signature.</summary>
    Reserved = 2,
    /// <summary>Signed in CEH, or sold out in the webshop.</summary>
    Confirmed = 3,
}

/// <summary>One drawn booth with its live state.</summary>
public sealed record ShopBoothView(
    ShopBoothSpot Spot, ShopProduct? Product, ShopBoothState State, string? CompanyName, bool Live);

/// <summary>
/// §1305 item 11 — the graphical availability view: every booth drawn on the expo layout, coloured by its LIVE
/// state. Operator: <i>"i know already the expo layout so I need to have a graphical view, where i can mark which
/// products are reserved/confirmed … mark it as how managers order – they look at the spot and book it."</i>
/// </summary>
/// <remarks>
/// Reserved vs confirmed comes from CEH's own orders; "sold" from the webshop's LIVE stock, so a booth sold in the
/// old shop shows as taken here immediately. Company names are only returned when asked for (organizer view): the
/// public map shows what is free, not who bought what.
/// </remarks>
public sealed class ShopBoothMapService
{
    private readonly CommunityHubDbContext _db;
    private readonly ShopStockService _stock;

    public ShopBoothMapService(CommunityHubDbContext db, ShopStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    public async Task<IReadOnlyList<ShopBoothView>> MapAsync(int eventId, bool withCompanyNames, CancellationToken ct = default)
    {
        var spots = await _db.ShopBoothSpots.AsNoTracking().Where(s => s.EventId == eventId).OrderBy(s => s.BoothId).ToListAsync(ct);
        if (spots.Count == 0) return Array.Empty<ShopBoothView>();

        var boothIds = spots.Select(s => s.BoothId).ToList();
        var products = await _db.ShopProducts.AsNoTracking().Include(p => p.PrimaryCategory)
            .Where(p => p.EventId == eventId && p.BoothId != null && boothIds.Contains(p.BoothId)
                        && p.Status == ShopProductStatus.Published)
            .ToListAsync(ct);

        // A booth id can appear on more than one product (the tier package AND a feature package that bundles it).
        // The tier package — the Booths group — is the one the map sells.
        var byBooth = products.GroupBy(p => p.BoothId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.PrimaryCategory?.Group == ShopGroup.Booths).First(),
                StringComparer.OrdinalIgnoreCase);
        var live = await _stock.GetAsync(byBooth.Values.ToList(), ct);

        var productIds = byBooth.Values.Select(p => p.Id).ToList();
        var held = await _db.ShopOrderLines.AsNoTracking()
            // §1312 — a contract that is only SENT holds nothing (stock is taken at signing): an unsigned order marks the
            // booth reserved only while its line actually holds the stock (an accepted reservation).
            .Where(l => productIds.Contains(l.ProductId) && l.Order.EventId == eventId
                        && (l.Order.Status == ShopOrderStatus.Signed
                            || (l.StockTaken && (l.Order.Status == ShopOrderStatus.Reserved || l.Order.Status == ShopOrderStatus.AwaitingSignature))))
            .Select(l => new { l.ProductId, l.Order.Status, l.Order.SponsorCompanyId })
            .ToListAsync(ct);
        var names = withCompanyNames
            ? await _db.ShopCompanies.AsNoTracking().Where(c => c.EventId == eventId)
                .ToDictionaryAsync(c => c.SponsorCompanyId, c => c.PublicLabel, ct)
            : new Dictionary<string, string>();

        var result = new List<ShopBoothView>();
        foreach (var spot in spots)
        {
            if (!byBooth.TryGetValue(spot.BoothId, out var product))
            {
                result.Add(new ShopBoothView(spot, null, ShopBoothState.NotForSale, null, true));
                continue;
            }
            var a = live.GetValueOrDefault(product.Id);
            var hold = held.FirstOrDefault(h => h.ProductId == product.Id);
            var state = hold is not null
                ? (hold.Status == ShopOrderStatus.Signed ? ShopBoothState.Confirmed : ShopBoothState.Reserved)
                : a is { Available: true } ? ShopBoothState.Free : ShopBoothState.Confirmed;
            var company = hold is not null && withCompanyNames ? names.GetValueOrDefault(hold.SponsorCompanyId) : null;
            result.Add(new ShopBoothView(spot, product, state, company, a?.Live ?? false));
        }
        return result;
    }
}
