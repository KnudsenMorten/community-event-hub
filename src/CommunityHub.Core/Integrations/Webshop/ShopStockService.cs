using CommunityHub.Core.Domain;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>§1305 — whether a product can be bought right now, and how sure we are.</summary>
/// <param name="Available">True only when the product is published AND in stock.</param>
/// <param name="Remaining">Units left, when CEH counts them. Null for unlimited products.</param>
/// <param name="Unlimited">CEH does not count this product (furniture, package handling, …).</param>
/// <param name="Live">
/// Always true now (§1312 — CEH is the one stock counter, so its answer is live). Kept for the callers: a checkout must
/// refuse a sale on a non-live answer, which is what prevented a double sale while the old webshop also sold.
/// </param>
public sealed record ShopAvailability(bool Available, int? Remaining, bool Unlimited, bool Live, DateTimeOffset? AsOf);

/// <summary>
/// §1305 — availability for the CEH sponsor shop. 🔴 §1312 (operator 2026-10-01: "we dont want to use woocommerce anymore.
/// ceh ows the webshop functionality 100%"): CEH is the ONE stock counter — every product answers from its own
/// <see cref="ShopProduct.ManageStock"/> / <see cref="ShopProduct.StockQuantity"/> / <see cref="ShopProduct.InStock"/>, which
/// <see cref="ShopOrderService"/> takes and gives back at the moment of a sale or release. The old webshop is closed and is
/// neither read nor written. The answer is always live (<see cref="ShopAvailability.Live"/>).
/// </summary>
public sealed class ShopStockService
{
    private readonly TimeProvider _clock;

    public ShopStockService(TimeProvider clock) => _clock = clock;

    /// <summary>Availability for each product, keyed by <see cref="ShopProduct.Id"/>.</summary>
    public Task<IReadOnlyDictionary<int, ShopAvailability>> GetAsync(
        IReadOnlyCollection<ShopProduct> products, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        IReadOnlyDictionary<int, ShopAvailability> result = products.ToDictionary(p => p.Id, p => new ShopAvailability(
            p.Status == ShopProductStatus.Published && p.InStock && (!p.ManageStock || p.StockQuantity is > 0),
            p.ManageStock ? p.StockQuantity : null, !p.ManageStock, true, now));
        return Task.FromResult(result);
    }
}
