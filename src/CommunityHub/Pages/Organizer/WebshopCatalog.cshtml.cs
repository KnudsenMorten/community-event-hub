using CommunityHub.Audit;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1305 — the organizer view of the CEH sponsor shop's catalogue: what was imported from the
/// webshop, what is still available, and which sponsor-facing group each category sits in.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-26: <i>"take the products from the current Webshop – same text, same
/// pictures, same prices, same availability – and migrate them over"</i> and <i>"use existing
/// categories from current Webshop and group them"</i>.</para>
///
/// <para>🔑 Always open to organizers, whether or not <c>sponsor-webshop</c> is on: the switch hides
/// the shop from SPONSORS and stops the scheduled sync, but an organizer has to be able to look at the
/// catalogue before deciding to switch it on.</para>
/// </remarks>
[Authorize]
public class WebshopCatalogModel : PageModel
{
    public const string FeatureKey = "sponsor-webshop";

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ShopStockService _stock;
    private readonly FeatureGateService _gate;
    private readonly TimeProvider _clock;

    public WebshopCatalogModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        ShopStockService stock,
        FeatureGateService gate,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _stock = stock;
        _gate = gate;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public bool FeatureOn { get; private set; }

    [TempData] public string? ActionMessage { get; set; }

    /// <summary>LIVE availability per product id, read from the webshop for this page view.</summary>
    public IReadOnlyDictionary<int, ShopAvailability> Stock { get; private set; } =
        new Dictionary<int, ShopAvailability>();

    /// <summary>False when the webshop could not be reached and availability is the last synced copy.</summary>
    public bool StockIsLive => Stock.Values.All(s => s.Live);

    /// <summary>Show hidden (private / draft / removed) products too.</summary>
    [BindProperty(SupportsGet = true)] public bool ShowHidden { get; set; }

    public IReadOnlyList<ShopCategory> Categories { get; private set; } = Array.Empty<ShopCategory>();
    public IReadOnlyList<ShopProduct> Products { get; private set; } = Array.Empty<ShopProduct>();
    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>The groups in the order a sponsor walks through them.</summary>
    public static readonly ShopGroup[] GroupOrder =
    {
        ShopGroup.Booths, ShopGroup.Packages, ShopGroup.Sessions, ShopGroup.Branding,
        ShopGroup.Furniture, ShopGroup.BoothOptions, ShopGroup.PackageHandling, ShopGroup.Swag, ShopGroup.Tickets,
        ShopGroup.OrganizerOnly, ShopGroup.Unassigned,
    };

    public static string GroupLabel(ShopGroup g) => g switch
    {
        ShopGroup.Booths => "Booths",
        ShopGroup.Packages => "Digital only sponsor (no booth)",
        ShopGroup.Sessions => "Sessions & track sponsorships",
        ShopGroup.Branding => "Branding & features",
        ShopGroup.Furniture => "Furniture",
        ShopGroup.BoothOptions => "Booth options",
        ShopGroup.OrganizerOnly => "Organizer only (hidden from sponsors)",
        ShopGroup.PackageHandling => "Package handling",
        ShopGroup.Swag => "Swag",
        ShopGroup.Tickets => "Tickets",
        _ => "Not grouped yet",
    };

    /// <summary>The group a product shows under: its primary category's group.</summary>
    public static ShopGroup GroupOf(ShopProduct p) => p.PrimaryCategory?.Group ?? ShopGroup.Unassigned;

    /// <summary>"Available", "Sold out", "N left", or "Removed from webshop".</summary>
    public static string AvailabilityText(ShopProduct p, ShopAvailability? a)
    {
        if (p.Status == ShopProductStatus.Removed) return "Removed from webshop";
        if (a is null || !a.Available) return "Sold out";
        if (a.Unlimited || a.Remaining is null) return "Available";
        return $"{a.Remaining} left";
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return; }
        await LoadAsync(me.EventId, ct);
    }

    [Audit("Changed the sponsor-facing group of a shop category",
        Category = AuditCategory.Admin, Action = "webshop.category-group", TargetType = "ShopCategory")]
    public async Task<IActionResult> OnPostGroupAsync(int categoryId, ShopGroup group, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null || !OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var cat = await _db.ShopCategories
            .FirstOrDefaultAsync(c => c.Id == categoryId && c.EventId == me.EventId, ct);
        if (cat is null)
        {
            ActionMessage = "That category no longer exists.";
            return RedirectToPage(new { ShowHidden });
        }

        cat.Group = group;
        cat.GroupSetByOrganizer = true;
        cat.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        ActionMessage = $"'{cat.Name}' now shows under {GroupLabel(group)}. The import will keep your choice.";
        return RedirectToPage(new { ShowHidden });
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        FeatureOn = await _gate.IsFeatureEnabledAsync(FeatureKey, eventId, ct);

        Categories = await _db.ShopCategories.AsNoTracking()
            .Where(c => c.EventId == eventId)
            .OrderBy(c => c.ParentId.HasValue).ThenBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct);

        var q = _db.ShopProducts.AsNoTracking()
            .Include(p => p.PrimaryCategory)
            .Include(p => p.Images)
            .Where(p => p.EventId == eventId);
        if (!ShowHidden) q = q.Where(p => p.Status == ShopProductStatus.Published);

        Products = await q.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync(ct);
        Stock = await _stock.GetAsync(Products, ct);
        LastSyncedAt = await _db.ShopProducts.AsNoTracking()
            .Where(p => p.EventId == eventId)
            .MaxAsync(p => (DateTimeOffset?)p.LastSyncedAt, ct);
    }
}
