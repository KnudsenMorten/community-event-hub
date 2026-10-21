using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using CommunityHub.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;

namespace CommunityHub.Pages.Sponsor.Shop;

/// <summary>
/// §1305 item 12 — the company's orders from BOTH shops: the CEH shop's own, and the old webshop's.
/// </summary>
[Authorize]
public class OrdersModel : ShopPageModel
{
    private readonly ShopOrderService _orders;
    private readonly IWebshopOrderSource _oldOrders;
    private readonly IMemoryCache _cache;

    public OrdersModel(
        ICurrentParticipantAccessor participant, FeatureGateService gate, SponsorActingCompanyService acting,
        ShopCompanyService companies, ShopOrderService orders, IWebshopOrderSource oldOrders,
        IMemoryCache cache)
        : base(participant, gate, acting, companies)
    {
        _orders = orders;
        _oldOrders = oldOrders;
        _cache = cache;
    }

    public IReadOnlyList<(ShopOrder Order, ShopTotals Totals)> ShopOrders { get; private set; } = Array.Empty<(ShopOrder, ShopTotals)>();
    public IReadOnlyList<WooOrder> WebshopOrders { get; private set; } = Array.Empty<WooOrder>();
    public string? WebshopError { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return;

        var list = new List<(ShopOrder, ShopTotals)>();
        foreach (var o in (await _orders.OrdersAsync(EventId, CompanyId!, ct)).Where(o => o.Lines.Count > 0 || o.Status != ShopOrderStatus.Cart))
            list.Add((o, await _orders.PriceAsync(o, Company!, ct)));
        ShopOrders = list;

        try
        {
            // Same cache key as /Sponsor (Index): one webshop fetch serves both pages.
            var all = await _cache.GetOrCreateAsync("woo:completed-orders", async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await _oldOrders.GetOrdersAsync("completed", ct);   // §1312 — CEH's store of the old webshop's orders
            });
            WebshopOrders = (all ?? Array.Empty<WooOrder>())
                .Where(o => string.Equals(o.CompanyId, CompanyId, StringComparison.Ordinal))
                .OrderByDescending(o => o.CreatedAt).ToList();
        }
        catch (Exception)
        {
            WebshopError = "The webshop's orders could not be read just now.";
        }
    }
}
