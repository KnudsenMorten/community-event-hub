using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
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

    public SwagCatalogModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SponsorSwagCatalogService catalog,
        FeatureGateService gate,
        SwagCatalogHoldService holds,
        SwagCatalogCreditService credits,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _catalog = catalog;
        _gate = gate;
        _holds = holds;
        _credits = credits;
        _clock = clock;
    }

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
        var released = await _gate.IsFeatureEnabledAsync(FeatureKey, me.EventId, ct);

        if (!released && !isOrganizer)
        {
            // 🔒 Not "no such page" and not a 403 — the same friendly notice the other sponsor
            // pages give, because a sponsor arriving here has done nothing wrong.
            NotAvailableYet = true;
            return;
        }

        IsUnreleasedPreview = !released && isOrganizer;

        if (me.Role != ParticipantRole.Sponsor && !isOrganizer)
        {
            WrongRole = true;
            return;
        }

        var now = _clock.GetUtcNow();
        var result = await _catalog.GetAsync(ct);
        Items = result.Items;
        BagQuantity = result.BagQuantity;
        CurrencySymbol = result.CurrencySymbol;
        // The diagnostic is operator language; a sponsor just sees an empty catalogue.
        if (isOrganizer) Diagnostic = result.Diagnostic;

        // §1165 — "sponsor can choose to upload logo or reuse already uploaded options". This is
        // the REUSE side, and it needs no new storage: the logo the company has already given us
        // is on SponsorInfo. Upload comes with the next slice.
        var companyId = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
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
