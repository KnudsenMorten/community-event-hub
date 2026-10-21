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

/// <summary>
/// §1305 item 9 — the sponsor shop: products by group (Booths, Furniture, Swag, Package handling …) with
/// pictures, prices and LIVE availability.
/// </summary>
[Authorize]
public class IndexModel : ShopPageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ShopStockService _stock;
    private readonly ShopOrderService _orders;

    public IndexModel(
        ICurrentParticipantAccessor participant, FeatureGateService gate, SponsorActingCompanyService acting,
        ShopCompanyService companies, CommunityHubDbContext db, ShopStockService stock, ShopOrderService orders)
        : base(participant, gate, acting, companies)
    {
        _db = db;
        _stock = stock;
        _orders = orders;
    }

    [BindProperty(SupportsGet = true)] public ShopGroup? Group { get; set; }

    /// <summary>Narrows the group to products whose name or category contains this text (the menu's
    /// "Buy more Exhibitor tickets" uses <c>Find=staff</c>).</summary>
    [BindProperty(SupportsGet = true)] public string? Find { get; set; }
    [TempData] public string? Message { get; set; }
    [TempData] public bool MessageIsError { get; set; }

    public IReadOnlyList<ShopGroup> Groups { get; private set; } = Array.Empty<ShopGroup>();
    public IReadOnlyList<ShopProduct> Products { get; private set; } = Array.Empty<ShopProduct>();
    public IReadOnlyDictionary<int, ShopAvailability> Stock { get; private set; } = new Dictionary<int, ShopAvailability>();

    /// <summary>§1308 — products held by a reservation: labelled "Reserved", not "Sold out".</summary>
    public IReadOnlySet<int> ReservedIds { get; private set; } = new HashSet<int>();
    public int CartCount { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return;

        var all = await _db.ShopProducts.AsNoTracking()
            .Include(p => p.PrimaryCategory).Include(p => p.Images)
            .Where(p => p.EventId == EventId && p.Status == ShopProductStatus.Published)
            .ToListAsync(ct);
        Groups = GroupOrder.Where(g => all.Any(p => p.PrimaryCategory?.Group == g)).ToList();
        Group ??= Groups.FirstOrDefault();

        var find = string.IsNullOrWhiteSpace(Find) ? null : Find.Trim();
        var inGroup = all.Where(p => Group is ShopGroup g && p.ListedUnder(g))   // §1312 — booths inside other packages too
            .Where(p => find is null || p.Name.Contains(find, StringComparison.OrdinalIgnoreCase)
                        || (p.PrimaryCategory?.Name ?? string.Empty).Contains(find, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.PrimaryCategory!.SortOrder).ThenBy(p => p.PrimaryCategory!.Name)
            .ThenBy(p => p.BoothId ?? p.Timeslot ?? string.Empty).ThenBy(p => p.Name)
            .ToList();
        Stock = await _stock.GetAsync(inGroup, ct);
        ReservedIds = await ShopReservationService.HeldProductIdsAsync(_db, EventId, inGroup.Select(p => p.Id).ToList(), ct);

        // Sold-out items are hidden unless the sponsor ticks "Show sold-out items" (operator 2026-09-26).
        var soldOut = inGroup.Where(p => Stock.GetValueOrDefault(p.Id) is not { Available: true }).ToList();
        SoldOutCount = soldOut.Count;
        Products = ShowSoldOut ? inGroup : inGroup.Except(soldOut).ToList();

        var cart = await _orders.CartAsync(EventId, CompanyId!, ct);
        CartCount = cart?.Lines.Sum(l => l.Quantity) ?? 0;
        if (cart is { Lines.Count: > 0 } && Company is not null)
            CartTotalEur = (await _orders.PriceAsync(cart, Company, ct)).SubtotalEur;
    }

    /// <summary>Show products that are sold out (off by default).</summary>
    [BindProperty(SupportsGet = true)] public bool ShowSoldOut { get; set; }
    public int SoldOutCount { get; private set; }
    public decimal? CartTotalEur { get; private set; }

    public async Task<IActionResult> OnPostAddAsync(int productId, int quantity, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        var r = await _orders.AddAsync(EventId, CompanyId!, productId, quantity, ParticipantId, ct);
        Message = r.Message;
        MessageIsError = !r.Ok;
        return RedirectToPage(new { Group, ShowSoldOut = ShowSoldOut ? true : (bool?)null });
    }
}
