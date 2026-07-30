using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>One distinct product a sponsor bought, and how many.</summary>
public sealed record SponsorPurchaseLine(string ProductName, int Quantity);

/// <summary>One sponsor's purchases of a thing.</summary>
/// <param name="Quantity">
/// Total units. 🔒 Meaningful for a SINGLE product (the TV — this is what gets ordered from the
/// venue). <b>Not meaningful for a CATEGORY:</b> the operator has 40+ Package Handling products, so
/// "3 storage + 2 handling + 1 customs = 6" is a number that reads like a quantity and tells him
/// nothing. For a category, show <see cref="Lines"/>.
/// </param>
/// <param name="Lines">
/// The distinct products behind <see cref="Quantity"/>, biggest first. This is the useful view for
/// shipment: WHICH services a sponsor bought, not how many things in total.
/// </param>
public sealed record SponsorPurchaseRow(
    string CompanyId,
    string CompanyName,
    int Quantity,
    IReadOnlyList<SponsorPurchaseLine> Lines);

/// <summary>
/// What the webshop could tell us. 🔒 THREE states, exactly like <c>TaskDataResult</c> — and for the
/// same reason (§555).
/// </summary>
/// <param name="Rows">Per-sponsor quantities, biggest first. Empty when nobody bought any.</param>
/// <param name="CouldNotCheck">
/// True when the lookup FAILED. 🔒 Distinguishing this from "nobody bought any" matters more here
/// than anywhere else in the system: the operator orders TV screens from the venue off
/// <see cref="Total"/>, so a failure rendering as a confident "0" means he under-orders and finds
/// out at check-in.
/// </param>
public sealed record SponsorPurchaseSummary(
    IReadOnlyList<SponsorPurchaseRow> Rows,
    bool CouldNotCheck,
    string? Reason = null)
{
    public int Total => Rows.Sum(r => r.Quantity);

    public static SponsorPurchaseSummary Unavailable(string reason) =>
        new(Array.Empty<SponsorPurchaseRow>(), true, reason);
}

/// <summary>
/// §687 / §666 — THE one place that answers "which sponsors bought this, and how many?".
/// </summary>
/// <remarks>
/// <para>🔒 <b>Shared deliberately between the sponsor's TASK and the organizer's LOGISTICS
/// panel.</b> §666 tells a sponsor how many TVs they have booked; §687 tells the operator how many
/// to order from the venue. Those two numbers must be the same number. Two independent queries over
/// the same orders is precisely how they drift — and the drift is invisible, because each screen
/// looks internally consistent.</para>
///
/// <para>Both lookups walk the SAME completed-order set the sponsor pipeline uses, so the edition's
/// <c>ordersAfter</c>/<c>ordersBefore</c> window applies and a previous edition's purchases can
/// never leak into this one's totals.</para>
/// </remarks>
public sealed class SponsorPurchaseSummaryService
{
    /// <summary>Short: the organizer refreshes this page while chasing sponsors.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    private readonly WooCommerceClient _woo;
    private readonly WooCommerceOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SponsorPurchaseSummaryService> _log;

    public SponsorPurchaseSummaryService(
        WooCommerceClient woo,
        WooCommerceOptions options,
        IMemoryCache cache,
        ILogger<SponsorPurchaseSummaryService> log)
    {
        _woo = woo;
        _options = options;
        _cache = cache;
        _log = log;
    }

    /// <summary>Per-sponsor quantities of ONE product id (§666 — the booth TV).</summary>
    public Task<SponsorPurchaseSummary> ByProductAsync(long productId, CancellationToken ct = default) =>
        SummariseAsync(
            $"product:{productId}",
            line => line.ProductId == productId,
            ct);

    /// <summary>
    /// Per-sponsor quantities of every line in a product CATEGORY (§687 — "Package Handling").
    /// </summary>
    /// <remarks>
    /// He named the CATEGORY, not a product id, and that is the right key here: shipment covers
    /// pre-event handling, post-event handling, storage and customs clearance as separate products,
    /// and a sponsor buying any of them needs to appear on the shipment list.
    /// </remarks>
    public Task<SponsorPurchaseSummary> ByCategoryAsync(string category, CancellationToken ct = default) =>
        SummariseAsync(
            $"category:{category}",
            line => line.CategoriesText
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(c => c.Equals(category, StringComparison.OrdinalIgnoreCase)),
            ct);

    private async Task<SponsorPurchaseSummary> SummariseAsync(
        string cacheKey, Func<WooLineItem, bool> matches, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return SponsorPurchaseSummary.Unavailable("the webshop integration is disabled");
        }

        if (_cache.TryGetValue<SponsorPurchaseSummary>($"purchases:{cacheKey}", out var hit)
            && hit is not null)
        {
            return hit;
        }

        SponsorPurchaseSummary summary;
        try
        {
            // enrichCategories: true — the ORDER payload carries no categories, so the category
            // lookup needs the products pass. It is batched and cached; the product-id lookup pays
            // for it too, which is cheaper than maintaining two order-fetch paths.
            var orders = await _woo.GetOrdersAsync("completed", ct, enrichCategories: true);

            var rows = orders
                .Where(o => !string.IsNullOrWhiteSpace(o.CompanyId))
                .SelectMany(o => o.LineItems
                    .Where(matches)
                    .Select(li => new { o.CompanyId, o.BillingCompany, li.ProductName, li.Quantity }))
                .GroupBy(x => x.CompanyId!, StringComparer.Ordinal)
                .Select(g => new SponsorPurchaseRow(
                    g.Key,
                    g.Select(x => x.BillingCompany).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                        ?? $"Company {g.Key}",
                    g.Sum(x => x.Quantity),
                    // The distinct services behind the total. A sponsor can buy the same product on
                    // two orders, so group by name and sum rather than listing the line twice.
                    g.GroupBy(x => x.ProductName, StringComparer.OrdinalIgnoreCase)
                        .Select(p => new SponsorPurchaseLine(p.Key, p.Sum(x => x.Quantity)))
                        .OrderByDescending(p => p.Quantity)
                        .ThenBy(p => p.ProductName, StringComparer.OrdinalIgnoreCase)
                        .ToList()))
                .OrderByDescending(r => r.Quantity)
                .ThenBy(r => r.CompanyName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            summary = new SponsorPurchaseSummary(rows, CouldNotCheck: false);
        }
        catch (Exception ex)
        {
            // 🔒 Fail LOUDLY as "could not check", never quietly as zero. The operator orders
            // hardware off this total.
            _log.LogError(ex, "SponsorPurchaseSummaryService: webshop lookup failed for {Key}.", cacheKey);
            summary = SponsorPurchaseSummary.Unavailable("the webshop could not be reached");
        }

        _cache.Set(
            $"purchases:{cacheKey}",
            summary,
            summary.CouldNotCheck ? TimeSpan.FromSeconds(20) : CacheFor);

        return summary;
    }
}
