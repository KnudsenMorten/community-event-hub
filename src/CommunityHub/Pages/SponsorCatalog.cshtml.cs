using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using Markdig;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// §1305 item 10b — the public sponsor catalogue (the replacement of the sponsor PowerPoint): a table of contents,
/// pages an organizer edits, and product lists with LIVE prices and availability.
/// </summary>
/// <remarks>
/// Operator: <i>"This is for a potential sponsor that has show interest. He should not be presented with login. It
/// must be possible for people to quickly move around in the presentation and go to a specific page product and
/// availability … Prices must be linked to Webshop + show availability in the catalog."</i>
/// 🔒 Anonymous, public layout (§27). Gated by <c>sponsor-webshop</c>: while it is off only a signed-in organizer
/// sees it. The booth map shows what is free; the "Reserved &amp; confirmed" page names who holds each package (§1312,
/// operator 2026-09-27: "names public is ok" — the sponsor deck showed "Sold to" publicly).
/// </remarks>
[AllowAnonymous]
public class SponsorCatalogModel : PageModel
{
    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().DisableHtml().Build();

    private readonly CommunityHubDbContext _db;
    private readonly ShopStockService _stock;
    private readonly ShopBoothMapService _map;
    private readonly FeatureGateService _gate;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ShopPackageAvailability _packages;

    public SponsorCatalogModel(
        CommunityHubDbContext db, ShopStockService stock, ShopBoothMapService map, FeatureGateService gate,
        ICurrentParticipantAccessor participant, ShopPackageAvailability packages, ShopSettings? shopSettings = null)
    {
        _packages = packages;
        _shopSettings = shopSettings;
        _db = db;
        _stock = stock;
        _map = map;
        _gate = gate;
        _participant = participant;
    }

    public bool Closed { get; private set; }
    public bool IsPreview { get; private set; }
    public string? EventName { get; private set; }
    public IReadOnlyList<ShopCatalogPage> Pages { get; private set; } = Array.Empty<ShopCatalogPage>();
    public ShopCatalogPage? Current { get; private set; }
    public string BodyHtml { get; private set; } = string.Empty;
    public IReadOnlyList<ShopProduct> Products { get; private set; } = Array.Empty<ShopProduct>();
    public IReadOnlyDictionary<int, ShopAvailability> Stock { get; private set; } = new Dictionary<int, ShopAvailability>();
    public Shared.ShopBoothMapViewModel? MapView { get; private set; }
    public ShopCatalogPage? Previous { get; private set; }
    public ShopCatalogPage? Next { get; private set; }
    /// <summary>§1312 — the live package lines: every package (an availability page) or this page's own (its product match).</summary>
    public IReadOnlyList<ShopPackageRow> Packages { get; private set; } = Array.Empty<ShopPackageRow>();
    public bool IsOrganizer { get; private set; }
    /// <summary>§1312 — the page describing each package (availability pages link to it).</summary>
    public IReadOnlyDictionary<ShopPackageRow, ShopCatalogPage?> PackagePages { get; private set; } = new Dictionary<ShopPackageRow, ShopCatalogPage?>();
    /// <summary>§1312 — the selling line under a package on the Availability page (config <c>packageHighlights</c>).</summary>
    public IReadOnlyDictionary<ShopPackageRow, string?> Highlights { get; private set; } = new Dictionary<ShopPackageRow, string?>();
    private readonly ShopSettings? _shopSettings;
    /// <summary>§1312 — the page's video as an in-page player (YouTube / Vimeo), when it is one.</summary>
    public string? VideoEmbedUrl { get; private set; }

    public async Task OnGetAsync(string? slug, CancellationToken ct)
    {
        // §1312 — live numbers: a refresh must always show the current state (operator: "so if i refresh it must be updated").
        Response.Headers.CacheControl = "no-store";
        var ev = await _db.Events.AsNoTracking().Where(e => e.IsActive).Select(e => new { e.Id, Name = e.DisplayName }).FirstOrDefaultAsync(ct);
        if (ev is null) { Closed = true; return; }
        EventName = ev.Name;
        var open = await _gate.IsFeatureEnabledAsync(ShopMailer.FeatureKey, ev.Id, ct);
        var me = _participant.Current;
        var organizer = me is not null && OrganizerAuth.IsRealOrganizer(me);
        IsOrganizer = organizer;
        IsPreview = !open && organizer;
        if (!open && !organizer) { Closed = true; return; }

        Pages = await _db.ShopCatalogPages.AsNoTracking()
            .Where(p => p.EventId == ev.Id && (p.IsPublished || organizer))
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Title).ToListAsync(ct);
        if (Pages.Count == 0) return;

        Current = string.IsNullOrWhiteSpace(slug) ? Pages[0] : Pages.FirstOrDefault(p => p.Slug == slug) ?? Pages[0];
        var idx = Pages.ToList().IndexOf(Current);
        Previous = idx > 0 ? Pages[idx - 1] : null;
        Next = idx < Pages.Count - 1 ? Pages[idx + 1] : null;
        BodyHtml = string.IsNullOrWhiteSpace(Current.BodyMarkdown) ? string.Empty : Markdig.Markdown.ToHtml(Current.BodyMarkdown, Markdown);

        if (Current.ProductGroup is ShopGroup g)
        {
            // §1312 — the same listing rule as the shop (ShopProduct.ListedUnder): the Booths page also shows booths sold
            // inside another package (the lounge sponsorship with booth E-27).
            Products = (await _db.ShopProducts.AsNoTracking().Include(p => p.PrimaryCategory).Include(p => p.Images)
                    .Where(p => p.EventId == ev.Id && p.Status == ShopProductStatus.Published
                                && (p.PrimaryCategory!.Group == g || (g == ShopGroup.Booths && p.BoothId != null)))
                    .ToListAsync(ct))
                .Where(p => p.ListedUnder(g))
                .OrderBy(p => p.PriceEur).ThenBy(p => p.Name).ToList();
            Stock = await _stock.GetAsync(Products, ct);
        }
        VideoEmbedUrl = EmbedUrl(Current.MediaUrl);
        // §1312 — who holds a package is public, as the deck's "Sold to" was (operator: "names public is ok").
        if (Current.AvailabilityView != ShopCatalogAvailabilityView.None)
        {
            Packages = await _packages.RowsAsync(ev.Id, null, withCompanyNames: true, ct);
            // Each package links to the page that says what is included.
            PackagePages = Packages.ToDictionary(r => r, r => ShopPackageAvailability.PageFor(r, Pages));
            // §1312 — the selling line under each package (edition config; operator: "make it more selling as managers see this").
            var highlights = _shopSettings is { IsConfigured: true } ? _shopSettings.Shop().PackageHighlights : new Dictionary<string, string>();
            Highlights = Packages.ToDictionary(r => r, r => highlights.Where(h => !h.Key.StartsWith("_", StringComparison.Ordinal))
                .FirstOrDefault(h => r.Products.Any(p => ShopPackageAvailability.Matches(p, h.Key))).Value);
        }
        else if (!string.IsNullOrWhiteSpace(Current.ProductMatch))
            Packages = await _packages.RowsAsync(ev.Id, Current.ProductMatch, withCompanyNames: false, ct);

        if (Current.ShowBoothMap)
            MapView = new Shared.ShopBoothMapViewModel(await _map.MapAsync(ev.Id, withCompanyNames: false, ct), null);
    }

    /// <summary>
    /// §1312 — a YouTube or Vimeo link as its player URL, so the video plays in the page (operator: the video "doesnt
    /// autoplay correctly" when it was only a link). Anything else ⇒ null and the page keeps the plain link.
    /// </summary>
    public static string? EmbedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var host = u.Host.ToLowerInvariant();
        string? id = null;
        if (host == "youtu.be") id = u.AbsolutePath.Trim('/');
        else if (host.EndsWith("youtube.com", StringComparison.Ordinal))
        {
            var v = System.Web.HttpUtility.ParseQueryString(u.Query)["v"];
            var m = System.Text.RegularExpressions.Regex.Match(u.AbsolutePath, @"^/(embed|shorts|live)/([^/]+)");
            id = !string.IsNullOrEmpty(v) ? v : m.Success ? m.Groups[2].Value : null;
        }
        else if (host.EndsWith("vimeo.com", StringComparison.Ordinal))
        {
            var m = System.Text.RegularExpressions.Regex.Match(u.AbsolutePath, @"/(\d+)");
            return m.Success ? $"https://player.vimeo.com/video/{m.Groups[1].Value}" : null;
        }
        return !string.IsNullOrEmpty(id) && System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]{6,20}$")
            ? $"https://www.youtube-nocookie.com/embed/{id}?rel=0"
            : null;
    }
}
