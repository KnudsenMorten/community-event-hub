using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Tasks;

/// <summary>
/// Builds the <c>{{placeholder}}</c> map for one sponsor company, at RENDER time.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This is the render-time twin of <c>SponsorOrderPullService.SubstitutePlaceholders</c>,
/// and it resolves the same four layers in the same order</b> — per-task upload URLs (falling back
/// to the generic upload portal when SharePoint did not provision one), per-company, per-tier, then
/// the edition's facts and cross-cutting placeholder map. The pull resolves them once and bakes the
/// result into <c>Tasks.Description</c>; this resolves them every time the task is rendered, which
/// is what §684.1 means by moving resolution from seed time to render time.</para>
///
/// <para><c>{{boothNumber}}</c> comes from <c>SponsorInfo.BoothLabel</c>, which the pull already
/// persists (§41a — it exists so Zoho receives a real <c>booth_label</c> on exhibitor create).
/// Reading the stored value rather than re-deriving it from the product name means the box marking
/// on the shipment task and the booth Zoho shows cannot drift apart.</para>
///
/// <para>🔒 A placeholder this builder cannot supply renders as EMPTY, never as literal
/// <c>{{braces}}</c> — right for the reader, invisible to everyone else. <see cref="ReportMissing"/>
/// is the only thing standing between that and silence, so call it at every render site.</para>
/// </remarks>
public sealed class SponsorTaskPlaceholderBuilder
{
    private readonly CommunityHubDbContext _db;
    private readonly EventEditionConfigLoader _eventConfig;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly SponsorConfigLoader _sponsorConfig;
    private readonly SponsorConfigOptions _sponsorConfigOptions;
    private readonly ILogger<SponsorTaskPlaceholderBuilder> _log;
    private readonly Settings.FeatureGateService? _gate;
    private readonly Integrations.Webshop.ShopSettings? _shop;

    /// <summary>The furniture tab of the CEH shop — where the furniture task lands.</summary>
    public const string CehShopFurnitureUrl = "/Sponsor/Shop?Group=Furniture";

    /// <summary>§1305 item 14 — where the shop buttons point once the CEH shop is switched on.</summary>
    public const string CehShopUrl = "/Sponsor/Shop";

    public SponsorTaskPlaceholderBuilder(
        CommunityHubDbContext db,
        EventEditionConfigLoader eventConfig,
        EventConfigOptions eventConfigOptions,
        SponsorConfigLoader sponsorConfig,
        SponsorConfigOptions sponsorConfigOptions,
        ILogger<SponsorTaskPlaceholderBuilder> log,
        // §1305 item 14 — optional so existing constructions and tests are unchanged.
        Settings.FeatureGateService? gate = null,
        Integrations.Webshop.ShopSettings? shop = null)
    {
        _gate = gate;
        _shop = shop;
        _db = db;
        _eventConfig = eventConfig;
        _eventConfigOptions = eventConfigOptions;
        _sponsorConfig = sponsorConfig;
        _sponsorConfigOptions = sponsorConfigOptions;
        _log = log;
    }

    /// <summary>Build the map for one company.</summary>
    public async Task<IReadOnlyDictionary<string, string>> BuildAsync(
        int eventId, string? sponsorCompanyId, CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // (4) Edition facts + the cross-cutting placeholder map. Added FIRST so the per-company and
        // per-tier layers below OVERWRITE them — same precedence as the pull's substitution order,
        // where the earlier passes have already consumed their markers by the time this map runs.
        var facts = _eventConfig.Load(_eventConfigOptions.EventConfigPath);
        if (facts.Placeholders is { Count: > 0 } placeholders)
        {
            foreach (var (key, value) in placeholders) map[key] = value ?? string.Empty;
        }
        map["expectedAttendees"] = facts.ExpectedAttendees.ToString();
        map["editionCode"] = facts.Code ?? string.Empty;
        map["editionCodeLower"] = (facts.Code ?? string.Empty).ToLowerInvariant();

        // §1305 item 14 — "the current tasks that all points to the old Webshop [must] use the new Webshop".
        // The furniture / shipment / TV / bag-content / booth-member tasks all link through {{configuratorUrl}};
        // while the CEH shop is ON it points at /Sponsor/Shop (an in-hub link, so no "different system"
        // hand-off dialog). While it is OFF nothing changes — the old webshop keeps working for ELDK27.
        var cehShop = _gate is not null && await _gate.IsFeatureEnabledAsync("sponsor-webshop", eventId, ct);
        if (cehShop)
        {
            map["configuratorUrl"] = CehShopUrl;
            // §1305 — the tasks talk about the CEH shop only (operator 2026-09-26: "verify that tasks refers only to
            // new webshop"). The old webshop's words are the defaults in event.<edition>.json → placeholders.
            map["shopName"] = "Sponsor Shop";
            map["shopPortalName"] = "Sponsor Shop";
            map["shopSignHint"] = "Your order becomes a contract that you read in full and sign here in the hub — or download it as a PDF and sign it your own way.";
            map["furnitureIntro"] = "The furniture included with your booth is taken off your cart automatically — anything beyond it is invoiced separately.";
            map["furnitureCouponSection"] = string.Empty;
            // Each order task opens the shop ON its product (operator 2026-09-26: "verify it links to the relevant
            // category in the webshop so it is easy to order").
            map["furnitureShopUrl"] = CehShopFurnitureUrl;
            map["bagShopUrl"] = "/Sponsor/Shop?Group=BoothOptions&Find=bag";
            map["tvShopUrl"] = "/Sponsor/Shop?Group=BoothOptions&Find=TV";
            map["staffShopUrl"] = "/Sponsor/Shop?Group=BoothOptions&Find=staff";
            map["shipmentShopUrl"] = "/Sponsor/Shop?Group=PackageHandling";
            const string cartSteps = "* Add it to your cart and adjust the quantity if needed.\n"
                + "* In the cart, send the contract to your signer (or sign it yourself if that is you).";
            map["bagOrderSteps"] = "* The button above opens the shop on **Attendee Bag Content Packaging**.\n" + cartSteps;
            map["tvOrderSteps"] = "* The button above opens the shop on the **TV screen** package.\n" + cartSteps;
            map["staffOrderSteps"] = "* The button above opens the shop on the **extra exhibitor staff tickets**.\n" + cartSteps;
            map["shipmentOrderSteps"] = "* The button above opens the shop on **Package Handling** — pick the services you need.\n" + cartSteps;
            map["furnitureOrderSteps"] =
                "1. Open the Sponsor Shop's **Furniture** tab — it has photos of every chair and table.\n"
                + "2. Add the items you want to your cart and adjust the quantity if needed.\n"
                + "3. The furniture included with your booth is deducted in the cart automatically — no code needed.\n"
                + "4. Send the contract to your signer from the cart (or sign it yourself if that is you).\n"
                + "5. Read the contract in full and sign it in the hub — or download the PDF and sign it your own way.";
        }

        if (string.IsNullOrWhiteSpace(sponsorCompanyId)) return map;

        // (1) Per-company.
        var info = await _db.SponsorInfos
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.EventId == eventId && s.SponsorCompanyId == sponsorCompanyId, ct);

        map["companyName"] = info?.CompanyName ?? string.Empty;

        // {{boothNumber}} — the physical booth slot ("E-26"). The pull parses it from the booth
        // product name and PERSISTS it on SponsorInfo.BoothLabel (§41a, where it exists so Zoho gets
        // a real booth_label on exhibitor create). Same value the pull substitutes, read from the
        // row rather than re-derived, so the box marking on the shipment task and the booth shown
        // in Zoho can never disagree.
        map["boothNumber"] = info?.BoothLabel ?? string.Empty;

        // (2) Per-tier: the matching boothWallSpecs row. furnitureSpec can itself contain
        // {{couponCode}}, which resolves because the renderer walks placeholder NODES rather than
        // doing ordered string replacement — the nesting the pull had to sequence carefully is not
        // a sequencing question here at all.
        if (info is not null && info.Tier != BoothTier.None)
        {
            try
            {
                var wallSpecs = _sponsorConfig
                    .Load(_sponsorConfigOptions.SponsorConfigPath)
                    .BoothWallSpecs;

                // BoothTier.ToString() is PascalCase ("Gold") while the JSON keys are lowercase;
                // System.Text.Json replaces the dictionary instance and drops the initializer's
                // OrdinalIgnoreCase comparer, so the lookup must use a lowercased key.
                if (wallSpecs?.Tiers is { Count: > 0 } tiers
                    && tiers.TryGetValue(info.Tier.ToString().ToLowerInvariant(), out var spec))
                {
                    map["furnitureSpec"] = spec.FurnitureSpec ?? string.Empty;
                    map["wallSpecUrl"] = spec.SpecUrl ?? string.Empty;
                    map["couponCode"] = spec.Coupon ?? string.Empty;
                    map["wallSize"] = spec.WallSize ?? string.Empty;
                }
            }
            catch (FileNotFoundException)
            {
                // Sponsor config missing is already loud elsewhere; a task rendering without its
                // tier values must not be an exception on a participant's page (§682).
            }

            // §1305 — in the CEH shop there is no coupon: the tier's furniture allowance is taken off
            // the cart automatically, and the furniture list comes from the live catalogue instead of
            // hand-written JSON (operator 2026-09-26: "the voucher idea dropped if sponsor uses the new
            // webshop … the tasks can point to the new webshop and list all furnitures automatically").
            if (cehShop)
            {
                map["couponCode"] = string.Empty;
                map["furnitureSpec"] = await CehFurnitureSpecAsync(eventId, info.Tier, ct);
            }
        }

        // (0) Per-task SharePoint upload-folder URLs, keyed by the folder key the body names
        // ({{wallFolderUrl}}, {{logoFolderUrl}}). A blank/unprovisioned URL falls back to the
        // generic upload portal, exactly as the pull does — a task that links nowhere is worse than
        // one that links to the general folder.
        var fallback = map.GetValueOrDefault("uploadPortalUrl", string.Empty);
        var locations = await _db.SponsorUploadLocations
            .AsNoTracking()
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == sponsorCompanyId)
            .Select(l => new { l.FolderKey, l.EditLinkUrl })
            .ToListAsync(ct);

        foreach (var location in locations)
        {
            if (string.IsNullOrWhiteSpace(location.FolderKey)) continue;
            map[location.FolderKey] = string.IsNullOrWhiteSpace(location.EditLinkUrl)
                ? fallback
                : location.EditLinkUrl;
        }

        return map;
    }

    /// <summary>
    /// The furniture task's body in the CEH shop: the allowance, a button into the Furniture tab, and every
    /// furniture product currently available, with its price.
    /// </summary>
    private async Task<string> CehFurnitureSpecAsync(int eventId, BoothTier tier, CancellationToken ct)
    {
        decimal allowance = 0m;
        try
        {
            if (_shop is { IsConfigured: true })
                _shop.Shop().IncludedFurniture.AllowanceEurByTier.TryGetValue(tier.ToString().ToLowerInvariant(), out allowance);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            // The shop config is loud elsewhere; the task still renders its list.
        }

        var furniture = await _db.ShopProducts.AsNoTracking()
            .Where(p => p.EventId == eventId && p.Status == Domain.ShopProductStatus.Published
                && p.InStock && p.PrimaryCategory != null && p.PrimaryCategory.Group == Domain.ShopGroup.Furniture)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new { p.Name, p.PriceEur })
            .ToListAsync(ct);

        return CehFurnitureSpec(allowance, furniture.Select(f => (f.Name, f.PriceEur)).ToList());
    }

    /// <summary>The furniture task's text in the CEH shop — pure, so it can be pinned by a test.</summary>
    public static string CehFurnitureSpec(decimal allowance, IReadOnlyList<(string Name, decimal PriceEur)> furniture)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        if (allowance > 0)
            sb.Append("Your booth includes furniture worth **").Append(allowance.ToString("0.##", inv))
              .Append(" EURO**. There is no coupon code: the amount is taken off your cart automatically when you add furniture.\n\n");
        sb.Append("[Choose your furniture in the shop](").Append(CehShopFurnitureUrl).Append(")\n\n");
        if (furniture.Count > 0)
        {
            sb.Append("__Furniture available now__\n");
            foreach (var f in furniture)
                sb.Append("* ").Append(f.Name).Append(" – EURO ").Append(f.PriceEur.ToString("0.##", inv)).Append('\n');
            sb.Append('\n');
        }
        // The "you must place the order" note is in the task's own "Good to know" section.
        return sb.ToString();
    }

    /// <summary>
    /// Report placeholders a body asked for and this builder could not supply.
    /// </summary>
    /// <remarks>
    /// 🔒 An unresolved placeholder renders as NOTHING — never as literal <c>{{braces}}</c> — which
    /// is right for the reader and invisible to everyone else. A live task quietly missing its
    /// price, coupon code or shipping address would look entirely normal. This is the only thing
    /// standing between that and silence, so it logs at ERROR.
    /// </remarks>
    public void ReportMissing(string taskKey, IReadOnlyCollection<string> missing)
    {
        if (missing.Count == 0) return;

        _log.LogError(
            "Task '{TaskKey}' referenced {Count} placeholder(s) the hub cannot resolve: {Keys}. "
            + "They rendered as EMPTY on a participant-facing surface. Add them to the edition "
            + "config, or correct the body.",
            taskKey, missing.Count, string.Join(", ", missing));
    }
}
