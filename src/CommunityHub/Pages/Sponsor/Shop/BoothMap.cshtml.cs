using CommunityHub.Auth;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using CommunityHub.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CommunityHub.Pages.Sponsor.Shop;

/// <summary>
/// §1305 item 11 — pick a booth on the expo map ("they look at the spot and book it"). A booth is optional:
/// branding-only sponsorships need none.
/// </summary>
[Authorize]
public class BoothMapModel : ShopPageModel
{
    private readonly ShopBoothMapService _map;

    public BoothMapModel(
        ICurrentParticipantAccessor participant, FeatureGateService gate, SponsorActingCompanyService acting,
        ShopCompanyService companies, ShopBoothMapService map)
        : base(participant, gate, acting, companies) => _map = map;

    [BindProperty(SupportsGet = true)] public string? Highlight { get; set; }
    public IReadOnlyList<ShopBoothView> Booths { get; private set; } = Array.Empty<ShopBoothView>();
    public Pages.Shared.ShopBoothMapViewModel? MapView { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!await ResolveAsync(ct)) return;
        Booths = await _map.MapAsync(EventId, withCompanyNames: IsOrganizer, ct);
        MapView = new Pages.Shared.ShopBoothMapViewModel(
            Booths, b => Url.Page("/Sponsor/Shop/Product", new { id = b.Product!.Id })!, Highlight, ShowList: IsOrganizer);
    }
}
