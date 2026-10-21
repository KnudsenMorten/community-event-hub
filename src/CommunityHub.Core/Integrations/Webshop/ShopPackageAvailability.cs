using System.Text.RegularExpressions;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// Who holds one slot of a package, and for how much: the order line's frozen price × quantity (+ surcharge), or the
/// product's price when it was sold in the old webshop. The amount is for organizers only (agreed prices are private).
/// </summary>
public sealed record ShopPackageTaker(string? Slot, string? Company, bool Confirmed, decimal AmountEur = 0);

/// <summary>
/// §1312 — one booth / timeslot / unit of a package on the Availability page, coloured by <see cref="State"/> (operator
/// 2026-09-28: "merge reserved/taken/available into one page/table/graphic … much easier to communicate with colors").
/// </summary>
public sealed record ShopPackageSlot(string? Label, ShopBoothState State, string? Company);

/// <summary>
/// One package line of the live availability table — the deck's "Actual Reservations" row: Package | Price | Remaining |
/// Maximum | Sold to. A package sold as slots (booths E-1…E-5, session timeslots) is ONE line.
/// </summary>
public sealed record ShopPackageRow(
    string Label, ShopGroup Group, ShopProduct PriceProduct, IReadOnlyList<ShopProduct> Products,
    int Available, int Reserved, int Confirmed, bool Unlimited, bool Live, IReadOnlyList<ShopPackageTaker> Takers,
    IReadOnlyList<string>? FreeSlots = null, IReadOnlyList<ShopPackageSlot>? Slots = null)
{
    /// <summary>The package's maximum: what is left plus what is taken (none for an unlimited package).</summary>
    public int? Maximum => Unlimited ? null : Available + Reserved + Confirmed;

    /// <summary>Money booked on this package: reserved + confirmed (organizers only).</summary>
    public decimal BookedEur => Takers.Sum(t => t.AmountEur);
    public decimal ConfirmedEur => Takers.Where(t => t.Confirmed).Sum(t => t.AmountEur);
}

/// <summary>
/// §1312 — live availability per PACKAGE for the sponsor catalogue, replacing the deck's hand-kept "Actual Reservations"
/// slides. Operator 2026-09-27: <i>"i also need an availability like in the powerpoint for all reservations / confirmed
/// orders"</i> · <i>"show both available and reserved/confirmed - 2 views"</i>.
/// </summary>
/// <remarks>
/// The same rule as the booth map (<see cref="ShopBoothMapService"/>): reserved vs confirmed comes from CEH's own orders
/// (a signed order, or an unsigned one while its line holds the stock); anything else the webshop reports as gone was
/// sold there and counts as confirmed. Company names are only returned when asked for (organizer view).
/// </remarks>
public sealed class ShopPackageAvailability
{
    /// <summary>The groups the deck's reservation slides covered, in the deck's order.</summary>
    public static readonly IReadOnlyList<ShopGroup> Groups =
        new[] { ShopGroup.Booths, ShopGroup.Packages, ShopGroup.Sessions, ShopGroup.Branding, ShopGroup.BoothOptions };

    private readonly CommunityHubDbContext _db;
    private readonly ShopStockService _stock;
    private readonly IWebshopOrderSource? _orders;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _cache;
    private readonly Microsoft.Extensions.Logging.ILogger<ShopPackageAvailability>? _log;

    public ShopPackageAvailability(CommunityHubDbContext db, ShopStockService stock, IWebshopOrderSource? orders = null,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? cache = null, Microsoft.Extensions.Logging.ILogger<ShopPackageAvailability>? log = null)
    {
        _db = db;
        _stock = stock;
        _orders = orders;
        _cache = cache;
        _log = log;
    }

    /// <summary>
    /// The package lines of the edition — all of them, or only the products whose name/category carries every word of
    /// <paramref name="match"/> (a catalogue page's <see cref="ShopCatalogPage.ProductMatch"/>).
    /// </summary>
    public async Task<IReadOnlyList<ShopPackageRow>> RowsAsync(int eventId, string? match, bool withCompanyNames, CancellationToken ct = default)
    {
        var products = (await _db.ShopProducts.AsNoTracking().Include(p => p.PrimaryCategory).Include(p => p.Images)
                .Where(p => p.EventId == eventId && p.Status == ShopProductStatus.Published && p.PriceEur > 0)
                .ToListAsync(ct))
            .Where(p => p.PrimaryCategory is not null && Groups.Contains(p.PrimaryCategory.Group))
            .Where(p => match is null || Matches(p, match))
            .ToList();
        if (products.Count == 0) return Array.Empty<ShopPackageRow>();

        var live = await _stock.GetAsync(products, ct);
        var ids = products.Select(p => p.Id).ToList();
        var held = (await _db.ShopOrderLines.AsNoTracking()
                .Where(l => ids.Contains(l.ProductId) && l.Order.EventId == eventId
                            && (l.Order.Status == ShopOrderStatus.Signed
                                || (l.StockTaken && (l.Order.Status == ShopOrderStatus.Reserved || l.Order.Status == ShopOrderStatus.AwaitingSignature))))
                .Select(l => new { l.ProductId, l.Order.Status, l.Order.SponsorCompanyId, l.UnitPriceEur, l.Quantity, l.SurchargePercent })
                .ToListAsync(ct))
            .Select(h => new Hold(h.ProductId, h.Status == ShopOrderStatus.Signed, h.SponsorCompanyId,
                Math.Round(h.UnitPriceEur * h.Quantity * (1 + h.SurchargePercent / 100m), 2)))
            .ToList();
        var names = withCompanyNames
            ? await _db.ShopCompanies.AsNoTracking().Where(c => c.EventId == eventId)
                .ToDictionaryAsync(c => c.SponsorCompanyId, c => c.PublicLabel, ct)
            : null;
        var soldTo = names is null ? null : await SoldInWebshopAsync(eventId, products, names, ct);
        return BuildRows(products, live, held, names, soldTo);
    }

    /// <summary>
    /// §1312 — who bought a product in the OLD webshop: its paid orders (completed / processing, the edition's order
    /// window), product → the buying company's public name. Operator 2026-09-28: <i>"i'm missing the sponsor names for
    /// confirmed, important for my selling"</i> — booths sold there have no CEH order, so they showed no name.
    /// Read from CEH's store of the old webshop's orders (§1312), cached 5 minutes; a failure costs the names, never the page.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string>?> SoldInWebshopAsync(
        int eventId, IReadOnlyCollection<ShopProduct> products, IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        if (_orders is null) return null;
        try
        {
            async Task<IReadOnlyList<WooOrder>> Load()
            {
                // §1312 — the old webshop's orders from CEH's own store (the webshop is closed).
                var completed = await _orders.GetOrdersAsync("completed", ct, enrichCategories: false);
                var processing = await _orders.GetOrdersAsync("processing", ct, enrichCategories: false);
                return completed.Concat(processing).ToList();
            }
            var orders = _cache is null
                ? await Load()
                : await Microsoft.Extensions.Caching.Memory.CacheExtensions.GetOrCreateAsync(_cache, $"woo:sold-to:{eventId}", async e =>
                {
                    e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                    return await Load();
                }) ?? Array.Empty<WooOrder>();
            return SoldTo(products, orders, names);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1312 the webshop's orders could not be read; confirmed packages show without a buyer.");
            return null;
        }
    }

    /// <summary>Product id → the company that bought it in the webshop (public name first, the billing company otherwise).</summary>
    public static IReadOnlyDictionary<int, string> SoldTo(
        IReadOnlyCollection<ShopProduct> products, IEnumerable<WooOrder> orders, IReadOnlyDictionary<string, string> names)
    {
        var byWoo = products.Where(p => p.WooProductId is not null).GroupBy(p => p.WooProductId!.Value).ToDictionary(g => g.Key, g => g.First().Id);
        var result = new Dictionary<int, string>();
        foreach (var o in orders.OrderBy(o => o.CreatedAt))
        {
            var company = o.CompanyId is not null && names.TryGetValue(o.CompanyId, out var n) && !string.IsNullOrWhiteSpace(n)
                ? n : o.BillingCompany;
            if (string.IsNullOrWhiteSpace(company)) continue;
            foreach (var l in o.LineItems)
                if (byWoo.TryGetValue(l.ProductId, out var pid)) result[pid] = company.Trim();
        }
        return result;
    }

    /// <summary>A product held by a CEH order.</summary>
    public sealed record Hold(int ProductId, bool Signed, string SponsorCompanyId, decimal AmountEur = 0);

    /// <summary>The package lines (pure — the tests drive it directly).</summary>
    public static IReadOnlyList<ShopPackageRow> BuildRows(
        IReadOnlyCollection<ShopProduct> products, IReadOnlyDictionary<int, ShopAvailability> live,
        IReadOnlyCollection<Hold> held, IReadOnlyDictionary<string, string>? companyNames,
        IReadOnlyDictionary<int, string>? soldInWebshop = null)
    {
        var rows = new List<ShopPackageRow>();
        var packages = products.GroupBy(p => IsSlot(p) ? $"slot|{p.PrimaryCategoryId}|{PackageLabel(p.Name)}" : $"one|{p.Id}");
        foreach (var package in packages)
        {
            var items = package.OrderBy(p => p.BoothId, SlotOrder.Instance).ThenBy(p => p.Timeslot, SlotOrder.Instance).ThenBy(p => p.Name).ToList();
            int available = 0, reserved = 0, confirmed = 0;
            var unlimited = false;
            var isLive = true;
            var takers = new List<ShopPackageTaker>();
            // §1312 — WHICH booths / timeslots are still free (operator 2026-09-28: "list the products available here so
            // they can see booth numbers or timeslot available").
            var free = new List<string>();
            var slots = new List<ShopPackageSlot>();
            foreach (var p in items)
            {
                var a = live.GetValueOrDefault(p.Id);
                isLive &= a?.Live ?? false;
                var holds = held.Where(h => h.ProductId == p.Id).ToList();
                foreach (var h in holds)
                {
                    if (h.Signed) confirmed++; else reserved++;
                    takers.Add(new ShopPackageTaker(Slot(p), companyNames?.GetValueOrDefault(h.SponsorCompanyId), h.Signed, h.AmountEur));
                    slots.Add(new ShopPackageSlot(IsSlot(p) ? FreeSlotLabel(p) : null, h.Signed ? ShopBoothState.Confirmed : ShopBoothState.Reserved,
                        companyNames?.GetValueOrDefault(h.SponsorCompanyId)));
                }
                if (a is { Available: true } && !(IsSlot(p) && holds.Count > 0))   // a held booth/timeslot is not also free
                {
                    if (a.Unlimited) unlimited = true;
                    else available += a.Remaining ?? 1;
                    if (IsSlot(p)) free.Add(FreeSlotLabel(p));
                    slots.Add(new ShopPackageSlot(IsSlot(p) ? FreeSlotLabel(p) : null, ShopBoothState.Free, null));
                }
                else if (holds.Count == 0)
                {
                    confirmed++;                                     // gone in the webshop without a CEH order: sold there
                    takers.Add(new ShopPackageTaker(Slot(p), soldInWebshop?.GetValueOrDefault(p.Id), true, p.EffectivePriceEur));
                    slots.Add(new ShopPackageSlot(IsSlot(p) ? FreeSlotLabel(p) : null, ShopBoothState.Confirmed, soldInWebshop?.GetValueOrDefault(p.Id)));
                }
            }
            var first = items[0];
            var label = PackageLabel(first.Name);
            var suffix = Regex.Match(first.Name, @"\(([^()]+)\)\s*$");
            if (items.Count > 1 && suffix.Success && items.All(p => p.Name.TrimEnd().EndsWith(suffix.Value.TrimEnd(), StringComparison.Ordinal)))
                label += $" ({suffix.Groups[1].Value})";
            var priceProduct = items.OrderBy(p => p.SalePriceEur ?? p.PriceEur).First();
            rows.Add(new ShopPackageRow(label, first.PrimaryCategory?.Group ?? ShopGroup.Branding, priceProduct, items,
                available, reserved, confirmed, unlimited, isLive, takers, free, slots));
        }
        return rows
            .OrderBy(r => Groups.ToList().IndexOf(r.Group))
            // By NAME within a group, so related packages sit together — the pre-day master classes, the sponsor sessions
            // (operator 2026-09-28: "sort order should not be due to price but title of product so they are ordered together").
            .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"E-16", or "slot 2 (11:30-12:30)" — the time the product name carries.</summary>
    private static string FreeSlotLabel(ShopProduct p)
    {
        if (!string.IsNullOrWhiteSpace(p.BoothId)) return p.BoothId!;
        var time = !string.IsNullOrWhiteSpace(p.TimeslotTime) ? p.TimeslotTime
            : Regex.Match(p.Name, @"\(est\. time:\s*([^)]+)\)") is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        return time is null ? $"slot {p.Timeslot}" : $"slot {p.Timeslot} ({time})";
    }

    private static bool IsSlot(ShopProduct p) => !string.IsNullOrWhiteSpace(p.BoothId) || !string.IsNullOrWhiteSpace(p.Timeslot);

    private static string? Slot(ShopProduct p) =>
        !string.IsNullOrWhiteSpace(p.BoothId) ? p.BoothId : !string.IsNullOrWhiteSpace(p.Timeslot) ? $"slot {p.Timeslot}" : null;

    /// <summary>
    /// "Platinum Exhibitor ELDK27 - Booth E-1" → "Platinum Exhibitor"; "Coffee Sponsor ELDK27" → "Coffee Sponsor". The edition
    /// code and the slot part are not the package's name.
    /// </summary>
    public static string PackageLabel(string productName)
    {
        var s = productName;
        var dash = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) s = s[..dash];
        s = Regex.Replace(s, @"\s*\b[A-Z]{2,6}\d{2}\b", "");
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Page ↔ product matching
    // ------------------------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal) { "and", "the", "with", "w", "for", "of", "a", "an", "in" };

    /// <summary>
    /// Does <paramref name="product"/> belong on a page matching <paramref name="match"/>? Every word must be in the product's
    /// name or category ("Gold Exhibitor" → every Gold booth, shared ones included). Plurals match singulars ("Sponsor
    /// Sessions" → every session). A match made only of edition codes ("ELDK27") matches nothing.
    /// </summary>
    /// <remarks>
    /// §1312 (2026-09-28, "some are missing links"): several alternatives separated by <c>;</c>, and <c>=Name</c> for one
    /// package by its exact name ("Community Lounge Sponsor; =Lounge sponsor with Exhibitor Booth E-27 in lounge (feature size)").
    /// </remarks>
    public static bool Matches(ShopProduct product, string? match)
    {
        if (string.IsNullOrWhiteSpace(match)) return false;
        foreach (var alternative in Alternatives(match))
        {
            if (alternative.StartsWith('=')
                    ? string.Equals(PackageLabel(product.Name), PackageLabel(alternative[1..].Trim()), StringComparison.OrdinalIgnoreCase)
                    : AllWords(product, alternative))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> Alternatives(string match) =>
        match.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool AllWords(ShopProduct product, string match)
    {
        var need = Words(match);
        if (!need.Any(w => !IsEdition(w))) return false;
        var have = Words(product.Name + " " + product.PrimaryCategory?.Name).ToHashSet(StringComparer.Ordinal);
        return need.All(have.Contains);
    }

    /// <summary>How specific a match is: an exact-name alternative beats any word match; more words beat fewer.</summary>
    public static int Specificity(string match) =>
        Alternatives(match).Select(a => a.StartsWith('=') ? 1000 : Words(a).Count).DefaultIfEmpty(0).Max();

    /// <summary>
    /// §1312 — the catalogue page that describes a package line, so the summary can link to "what is included" (operator:
    /// "can the overview include links to the other pages, so they can see what is included"). The most specific match
    /// wins: "Sponsor Long 60 min Session" over "Sponsor Sessions".
    /// </summary>
    public static ShopCatalogPage? PageFor(ShopPackageRow row, IEnumerable<ShopCatalogPage> pages) =>
        pages.Where(p => p.AvailabilityView == ShopCatalogAvailabilityView.None && !string.IsNullOrWhiteSpace(p.ProductMatch)
                         && row.Products.Any(x => Matches(x, p.ProductMatch)))
            .OrderByDescending(p => Specificity(p.ProductMatch!))
            .ThenBy(p => p.SortOrder)
            .FirstOrDefault();

    private static List<string> Words(string s) =>
        Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+")
            .Where(w => w.Length > 0 && !StopWords.Contains(w))
            .Select(w => w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) ? w[..^1] : w)
            .ToList();

    private static bool IsEdition(string w) => Regex.IsMatch(w, @"^[a-z]{2,6}\d{2}$");

    /// <summary>E-2 before E-10; "slot 2" before "slot 10".</summary>
    private sealed class SlotOrder : IComparer<string?>
    {
        public static readonly SlotOrder Instance = new();
        public int Compare(string? x, string? y)
        {
            if (x == y) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var nx = Regex.Match(x, @"\d+");
            var ny = Regex.Match(y, @"\d+");
            if (nx.Success && ny.Success && x[..nx.Index] == y[..ny.Index])
            {
                var c = int.Parse(nx.Value).CompareTo(int.Parse(ny.Value));
                if (c != 0) return c;
            }
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }
    }
}
