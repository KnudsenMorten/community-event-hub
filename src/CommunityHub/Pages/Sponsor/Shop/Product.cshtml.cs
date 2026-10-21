using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using CommunityHub.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Sponsor.Shop;

/// <summary>§1305 item 4 — one product: full description, pictures, price conditions, live availability.</summary>
[Authorize]
public class ProductModel : ShopPageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ShopStockService _stock;
    private readonly ShopOrderService _orders;
    private readonly ShopSettings _settings;

    public ProductModel(
        ICurrentParticipantAccessor participant, FeatureGateService gate, SponsorActingCompanyService acting,
        ShopCompanyService companies, CommunityHubDbContext db, ShopStockService stock, ShopOrderService orders,
        ShopSettings settings)
        : base(participant, gate, acting, companies)
    {
        _db = db;
        _stock = stock;
        _orders = orders;
        _settings = settings;
    }

    public ShopProduct? Product { get; private set; }
    public ShopAvailability? Availability { get; private set; }

    /// <summary>§1308 — held by a reservation: labelled "Reserved", not "Sold out".</summary>
    public bool IsReserved { get; private set; }
    public decimal SurchargePercentNow { get; private set; }
    public string? SurchargeRule { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public bool MessageIsError { get; set; }

    public async Task OnGetAsync(int id, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return;
        Product = await _db.ShopProducts.AsNoTracking()
            .Include(p => p.PrimaryCategory).Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == id && p.EventId == EventId && p.Status == ShopProductStatus.Published, ct);
        if (Product is null) return;
        if (Product.PrimaryCategory?.Group == ShopGroup.OrganizerOnly) { Product = null; return; }
        Availability = (await _stock.GetAsync(new[] { Product }, ct)).GetValueOrDefault(Product.Id);
        IsReserved = (await ShopReservationService.HeldProductIdsAsync(_db, EventId, new[] { Product.Id }, ct)).Count > 0;

        var rule = _settings.Shop().LateSurcharge;
        var group = Product.PrimaryCategory?.Group ?? ShopGroup.Unassigned;
        SurchargePercentNow = ShopPricing.SurchargePercentFor(group, DateTimeOffset.UtcNow, _settings.EventStartsAt(), rule);
        if (rule.Groups.Any(g => string.Equals(g, group.ToString(), StringComparison.OrdinalIgnoreCase)) && rule.Percent > 0)
            SurchargeRule = $"Ordered less than {rule.DaysBeforeEvent} days before the event, this product costs +{rule.Percent:0}%.";
    }

    public async Task<IActionResult> OnPostAddAsync(int id, int quantity, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage(new { id });
        var r = await _orders.AddAsync(EventId, CompanyId!, id, quantity, ParticipantId, ct);
        Message = r.Message;
        MessageIsError = !r.Ok;
        return RedirectToPage(new { id });
    }
}
