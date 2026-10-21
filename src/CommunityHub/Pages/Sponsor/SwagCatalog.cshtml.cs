using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using CommunityHub.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// §1165 — the sponsor swag catalogue: brandable items for the attendee bags.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-01: <i>"a new sponsor swag catalog which can be used to put in attendees
/// bags … with prices, teaser text and picture"</i> · <i>"page must be hidden in menu for now until
/// i have approved it"</i>.</para>
///
/// <para>🔒 <b>Hidden means hidden from SPONSORS, not from him.</b> While the
/// <c>sponsor-swag-catalog</c> switch is off there is no nav entry anywhere and a sponsor who
/// reaches the URL is told it is not available — but an ORGANIZER sees the page in full, with a
/// preview banner, so he can approve the thing he is approving. Leaving it merely unlinked would
/// have been "not advertised", not "not released", and a guessable URL is not a permission.</para>
///
/// <para>🔑 <b>The catalogue is the webshop.</b> Price, picture, teaser and availability all come
/// from the product, so nothing here can disagree with what is actually for sale, and
/// <i>ordered ⇒ unavailable</i> is the shop's own stock rather than a rule this page invents.</para>
/// </remarks>
[Authorize]
public class SwagCatalogModel : PageModel
{
    /// <summary>The per-edition switch that releases this page to sponsors.</summary>
    public const string FeatureKey = "sponsor-swag-catalog";

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SponsorSwagCatalogService _catalog;
    private readonly FeatureGateService _gate;
    private readonly SwagCatalogHoldService _holds;
    private readonly SwagCatalogCreditService _credits;
    private readonly TimeProvider _clock;
    private readonly SponsorActingCompanyService _acting;
    private readonly CommunityHub.Core.Integrations.Webshop.ShopOrderService? _shopOrders;

    /// <summary>
    /// §1305 item 13 — true when the CEH shop is on: "Choose this" puts the item (for every bag) in the company's
    /// CEH order instead of sending the sponsor to the old webshop. Operator: "keep the swag catalog as i originally
    /// designed it … but end result is a cart entry + contract + signing + invoice".
    /// </summary>
    public bool OrderInCehShop { get; private set; }

    [TempData] public string? ShopMessage { get; set; }
    [TempData] public bool ShopMessageIsError { get; set; }

    /// <summary>§1305 item 13 — "Choose this": the item, for all bags, into the company's CEH cart.</summary>
    public async Task<IActionResult> OnPostAddToOrderAsync(long productId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || _shopOrders is null) return RedirectToPage();
        if (!await _gate.IsFeatureEnabledAsync(CommunityHub.Core.Integrations.Webshop.ShopMailer.FeatureKey, me.EventId, ct))
            return RedirectToPage();
        var companyId = await _acting.CurrentAsync(me.ParticipantId, me.EventId, ct);
        if (string.IsNullOrWhiteSpace(companyId)) return RedirectToPage();
        var bags = (await _catalog.GetAsync(ct)).BagQuantity;
        var r = await _shopOrders.AddSwagAsync(me.EventId, companyId!, productId, bags, me.ParticipantId, ct);
        ShopMessage = r.Message;
        ShopMessageIsError = !r.Ok;
        return RedirectToPage();
    }

    public SwagCatalogModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SponsorSwagCatalogService catalog,
        FeatureGateService gate,
        SwagCatalogHoldService holds,
        SwagCatalogCreditService credits,
        TimeProvider clock,
        SponsorActingCompanyService acting,
        // §1305 item 13 — the configurator's outcome becomes a CEH cart line (when the CEH shop is on).
        CommunityHub.Core.Integrations.Webshop.ShopOrderService? shopOrders = null,
        CommunityHub.Core.Integrations.Webshop.ShopSettings? shopSettings = null)
    {
        _shopOrders = shopOrders;
        _shopSettings = shopSettings;
        _db = db;
        _participant = participant;
        _catalog = catalog;
        _gate = gate;
        _holds = holds;
        _credits = credits;
        _clock = clock;
        _acting = acting;
    }

    private readonly CommunityHub.Core.Integrations.Webshop.ShopSettings? _shopSettings;

    /// <summary>
    /// §1305 — with the CEH shop on: what the sponsor pays per item (webshop product id → amount), i.e. their share of the
    /// item for every bag. Shown INSTEAD of the per-piece price and the full total (operator 2026-09-26).
    /// </summary>
    public IReadOnlyDictionary<long, CommunityHub.Core.Integrations.Webshop.ShopPricing.SwagSplit> Contribution { get; private set; } =
        new Dictionary<long, CommunityHub.Core.Integrations.Webshop.ShopPricing.SwagSplit>();

    public bool WrongRole { get; private set; }

    /// <summary>True when an organizer is previewing a catalogue not yet released to sponsors.</summary>
    public bool IsUnreleasedPreview { get; private set; }

    /// <summary>True when the caller may not see it at all (a sponsor, before release).</summary>
    public bool NotAvailableYet { get; private set; }

    public IReadOnlyList<SwagCatalogItem> Items { get; private set; } = Array.Empty<SwagCatalogItem>();

    /// <summary>
    /// Why the catalogue is empty, when it is empty for a reason. ⚠️ Shown to ORGANIZERS only — it
    /// names config keys and product counts, which is operator language, not sponsor language.
    /// </summary>
    public string? Diagnostic { get; private set; }

    /// <summary>The company's logo already on file, if any — the "reuse" half of his ask.</summary>
    public string? ExistingLogoFileName { get; private set; }

    /// <summary>
    /// How many pieces one bag order is — the multiplier behind the total price. 0 means unknown,
    /// and the page then shows no total rather than a wrong one.
    /// </summary>
    public int BagQuantity { get; private set; }

    /// <summary>The webshop's currency symbol, or null to show bare numbers.</summary>
    public string? CurrencySymbol { get; private set; }

    /// <summary>Per item: whether this company can take it, and who has it if not.</summary>
    public IReadOnlyDictionary<long, SwagItemState> States { get; private set; } =
        new Dictionary<long, SwagItemState>();

    /// <summary>
    /// The company's live credits. ⚠️ Usually EMPTY — operator 2026-09-02: <i>"coupon is optional,
    /// not all sponsors will get one"</i>. No credit means the block is simply absent, never a
    /// "you have €0" that reads as something withheld.
    /// </summary>
    public IReadOnlyList<SwagCatalogCredit> Credits { get; private set; } =
        Array.Empty<SwagCatalogCredit>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;

        var isOrganizer = OrganizerAuth.IsRealOrganizer(me);
        // §1305 — the configurator is also the CEH shop's Swag door: open whenever the shop is open, not only under
        // its own switch, so the Swag tab never leads to "not available".
        var cehShop = await _gate.IsFeatureEnabledAsync(CommunityHub.Core.Integrations.Webshop.ShopMailer.FeatureKey, me.EventId, ct);
        var released = cehShop || await _gate.IsFeatureEnabledAsync(FeatureKey, me.EventId, ct);

        if (!released && !isOrganizer)
        {
            // 🔒 Not "no such page" and not a 403 — the same friendly notice the other sponsor
            // pages give, because a sponsor arriving here has done nothing wrong.
            NotAvailableYet = true;
            return;
        }

        IsUnreleasedPreview = !released && isOrganizer;
        OrderInCehShop = _shopOrders is not null && cehShop;

        if (me.Role != ParticipantRole.Sponsor && !isOrganizer)
        {
            WrongRole = true;
            return;
        }

        var now = _clock.GetUtcNow();
        var result = await _catalog.GetAsync(ct);
        Items = result.Items;
        BagQuantity = result.BagQuantity;
        if (OrderInCehShop && _shopSettings is not null)
        {
            var ids = Items.Select(i => i.ProductId).ToList();
            var rule = _shopSettings.Shop().SwagPricing;
            Contribution = (await _db.ShopProducts.AsNoTracking()
                    .Where(p => p.EventId == me.EventId && p.WooProductId != null && ids.Contains(p.WooProductId.Value))
                    .ToListAsync(ct))
                .ToDictionary(p => p.WooProductId!.Value,
                    p => CommunityHub.Core.Integrations.Webshop.ShopPricing.SwagSplitFor(p, rule, Math.Max(1, BagQuantity)));
        }
        CurrencySymbol = result.CurrencySymbol;
        // The diagnostic is operator language; a sponsor just sees an empty catalogue.
        if (isOrganizer) Diagnostic = result.Diagnostic;

        // §1165 — "sponsor can choose to upload logo or reuse already uploaded options". This is
        // the REUSE side, and it needs no new storage: the logo the company has already given us
        // is on SponsorInfo. Upload comes with the next slice.
        // §1236 — the company this contact is ACTING for (switcher choice, re-validated), not just their primary.
        var companyId = await _acting.CurrentAsync(me.ParticipantId, me.EventId, ct);
        if (!string.IsNullOrWhiteSpace(companyId))
        {
            var info = await _db.SponsorInfos
                .Where(s => s.EventId == me.EventId && s.SponsorCompanyId == companyId)
                .Select(s => new { s.LogoVectorFileName, s.LogoRasterFileName })
                .FirstOrDefaultAsync(ct);

            ExistingLogoFileName = info?.LogoVectorFileName ?? info?.LogoRasterFileName;

            Credits = await _credits.LiveForCompanyAsync(
                me.EventId, companyId!, DateOnly.FromDateTime(now.UtcDateTime), ct);
        }

        // §1165b — join the shop's "sold" with CEH's "promised". The rule lives in SwagAvailability
        // so this page and the organizer roll-up cannot drift apart about what is free.
        var live = await _holds.LiveHoldsAsync(me.EventId, now, ct);
        var byProduct = live
            .GroupBy(h => h.ProductId)
            .ToDictionary(g => g.Key, g => g.First());

        States = Items.ToDictionary(
            i => i.ProductId,
            i => SwagAvailability.Resolve(
                i.Available,
                byProduct.TryGetValue(i.ProductId, out var h) ? h : null,
                companyId));
    }
}
