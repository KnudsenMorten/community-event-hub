using CommunityHub.Audit;
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
/// §1305 item 10 — "reserve what you had last time": the company's reservation rights from the previous
/// edition, reserved in one step while the window is open.
/// </summary>
[Authorize]
public class ReserveModel : ShopPageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ShopOrderService _orders;
    private readonly ShopStockService _stock;
    private readonly ShopSettings _settings;
    private readonly TimeProvider _clock;

    public ReserveModel(
        ICurrentParticipantAccessor participant, FeatureGateService gate, SponsorActingCompanyService acting,
        ShopCompanyService companies, CommunityHubDbContext db, ShopOrderService orders, ShopStockService stock,
        ShopSettings settings, TimeProvider clock)
        : base(participant, gate, acting, companies)
    {
        _db = db;
        _orders = orders;
        _stock = stock;
        _settings = settings;
        _clock = clock;
    }

    public IReadOnlyList<ShopEntitlement> Rights { get; private set; } = Array.Empty<ShopEntitlement>();
    public IReadOnlyDictionary<int, ShopAvailability> Stock { get; private set; } = new Dictionary<int, ShopAvailability>();
    public bool WindowOpen { get; private set; }
    public DateOnly? OpensOn { get; private set; }
    public DateOnly? ClosesOn { get; private set; }
    [TempData] public string? Message { get; set; }
    [TempData] public bool MessageIsError { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return;
        var window = _settings.Shop().Reservation;
        OpensOn = window.OpensOnDate;
        ClosesOn = window.ClosesOnDate;
        WindowOpen = window.IsOpen(DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime));
        Rights = await _db.ShopEntitlements.AsNoTracking()
            .Include(e => e.MatchedProduct)
            .Where(e => e.EventId == EventId && e.SponsorCompanyId == CompanyId)
            .OrderBy(e => e.ProductName).ToListAsync(ct);
        var products = Rights.Where(r => r.MatchedProduct is not null).Select(r => r.MatchedProduct!).DistinctBy(p => p.Id).ToList();
        Stock = await _stock.GetAsync(products, ct);
    }

    [Audit("Reserved last edition's items in the sponsor shop", Action = "webshop.reservation-claim", TargetType = "ShopEntitlement")]
    public async Task<IActionResult> OnPostReserveAsync(int[] rightIds, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        if (!_settings.Shop().Reservation.IsOpen(DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime)) && !IsOrganizer)
            return Done(ShopResult.Fail("The reservation window is closed."));

        var rights = await _db.ShopEntitlements
            .Where(e => rightIds.Contains(e.Id) && e.EventId == EventId && e.SponsorCompanyId == CompanyId
                        && e.ClaimedOrderLineId == null && e.MatchedProductId != null)
            .ToListAsync(ct);
        if (rights.Count == 0) return Done(ShopResult.Fail("Choose at least one item that can be reserved."));

        foreach (var r in rights)
        {
            var add = await _orders.AddAsync(EventId, CompanyId!, r.MatchedProductId!.Value, 1, ParticipantId, ct);
            if (!add.Ok) return Done(ShopResult.Fail($"{r.ProductName}: {add.Message}"));
        }
        var cart = await _orders.CartAsync(EventId, CompanyId!, ct);
        var reserved = await _orders.ReserveAsync(cart!, ct);
        return Done(reserved.Ok
            ? ShopResult.Success(reserved.Message + " Sign the contract from the cart page when you are ready.")
            : reserved);
    }

    public async Task<IActionResult> OnPostDeclineAsync(int rightId, CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return RedirectToPage();
        var right = await _db.ShopEntitlements.FirstOrDefaultAsync(e => e.Id == rightId && e.EventId == EventId && e.SponsorCompanyId == CompanyId, ct);
        if (right is null) return Done(ShopResult.Fail("Not found."));
        right.DeclinedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Done(ShopResult.Success($"Noted — you will not be reminded about '{right.ProductName}' again."));
    }

    private IActionResult Done(ShopResult r)
    {
        Message = r.Message;
        MessageIsError = !r.Ok;
        return RedirectToPage();
    }
}
