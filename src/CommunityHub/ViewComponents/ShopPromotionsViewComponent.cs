using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.ViewComponents;

/// <summary>One product in the teaser bar or the commercial corner.</summary>
public sealed record ShopPromoItem(ShopProduct Product, int? ImageId, string Href);

/// <summary>What the teaser bar / commercial corner render.</summary>
public sealed record ShopPromotionsView(string Kind, IReadOnlyList<ShopPromoItem> Items);

/// <summary>
/// §1305 — two ways the shop sells to a sponsor who is only browsing:
/// <list type="bullet">
///   <item><description><b>Teaser bar</b> (<c>kind: "teaser"</c>) — a strip running across the page with every AVAILABLE
///     branding option, session and swag item (operator 2026-09-26: <i>"a teaser bar … show available branding options +
///     sessions available, it should run from left to right as teaser for sponsor to buy it … only select those 2
///     categories and only available products … also include swag options"</i>).</description></item>
///   <item><description><b>Commercial corner</b> (<c>kind: "corner"</c>) — the (up to) three products the organizer chose
///     on /Organizer/ShopPromotions, one at a time with picture and price (<i>"promote 3 products that i want to get
///     sold"</i>). A sold-out product drops out by itself; the organizer is mailed to choose another.</description></item>
/// </list>
/// Availability comes from the catalogue copy the 2-minute sync keeps, so a page view never waits on the webshop.
/// Renders nothing when the shop is off, and never breaks the page it sits on.
/// </summary>
public sealed class ShopPromotionsViewComponent : ViewComponent
{
    public static readonly ShopGroup[] TeaserGroups = { ShopGroup.Branding, ShopGroup.Sessions, ShopGroup.Swag };
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly FeatureGateService _gate;

    public ShopPromotionsViewComponent(CommunityHubDbContext db, ICurrentParticipantAccessor participant, FeatureGateService gate)
    {
        _db = db;
        _participant = participant;
        _gate = gate;
    }

    public async Task<IViewComponentResult> InvokeAsync(string kind)
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            var me = _participant.Current;
            if (me is null || !await _gate.IsFeatureEnabledAsync(ShopMailer.FeatureKey, me.EventId, ct)) return Content(string.Empty);
            // The teaser sells to sponsors only — it sits under the menu on every page, so a speaker or attendee
            // must never see it.
            if (kind != "corner" && me.Role != ParticipantRole.Sponsor) return Content(string.Empty);

            var q = _db.ShopProducts.AsNoTracking().Include(p => p.PrimaryCategory).Include(p => p.Images)
                .Where(p => p.EventId == me.EventId && p.Status == ShopProductStatus.Published && p.InStock);
            List<ShopProduct> products;
            if (kind == "corner")
            {
                products = await q.Where(p => p.PromotedSlot != null).OrderBy(p => p.PromotedSlot).Take(3).ToListAsync(ct);
            }
            else
            {
                // Operator 2026-09-26: "drop the add-ons" — only counted-stock sponsorships (an unlimited product never
                // sells out, so it is an add-on, not something to chase). Pre-day products STAY ("pre-day must go in to teaser").
                products = await q.Where(p => p.PrimaryCategory != null && TeaserGroups.Contains(p.PrimaryCategory.Group) && p.ManageStock)
                    .OrderBy(p => p.PrimaryCategory!.Group).ThenBy(p => p.SortOrder).ThenBy(p => p.Name).Take(40).ToListAsync(ct);
            }
            if (products.Count == 0) return Content(string.Empty);

            var items = products.Select(p => new ShopPromoItem(p,
                p.Images.OrderBy(i => i.SortOrder).Select(i => (int?)i.Id).FirstOrDefault(),
                // Swag is bought in the configurator (all bags); everything else on its product page.
                p.PrimaryCategory?.Group == ShopGroup.Swag ? "/Sponsor/SwagCatalog" : $"/Sponsor/Shop/Product/{p.Id}"))
                .ToList();
            return View(kind == "corner" ? "Corner" : "Teaser", new ShopPromotionsView(kind, items));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Content(string.Empty);
        }
    }
}
