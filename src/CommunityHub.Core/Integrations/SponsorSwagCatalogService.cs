using System.Globalization;
using CommunityHub.Core.Config;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>One item in the sponsor swag catalogue, as a sponsor sees it.</summary>
/// <param name="Available">
/// Whether it can be taken right now. ⚠️ This is the SHOP's answer only — an organizer HOLD
/// (§1165b) is a separate layer the shop knows nothing about, and a caller that shows availability
/// must apply it on top.
/// </param>
public sealed record SwagCatalogItem(
    long ProductId,
    string Name,
    string? TeaserHtml,
    string? PriceText,
    string? ImageUrl,
    string? BuyUrl,
    bool Available,
    int? RemainingCount,
    decimal? UnitPrice = null,
    decimal? TotalPrice = null);

/// <summary>
/// What the catalogue read produced, including why it may be empty.
/// </summary>
/// <param name="ConfiguredCategory">The category the edition says holds the catalogue.</param>
/// <param name="Diagnostic">
/// 🔴 Set when the answer is empty for a reason a human needs to know — no category configured, or
/// a configured category that matches NO product. Null when the catalogue is genuinely just empty.
/// </param>
public sealed record SwagCatalogResult(
    IReadOnlyList<SwagCatalogItem> Items,
    string? ConfiguredCategory,
    string? Diagnostic,
    int BagQuantity = 0,
    string? CurrencySymbol = null)
{
    public IReadOnlyList<SwagCatalogItem> AvailableItems =>
        Items.Where(i => i.Available).ToList();
}

/// <summary>
/// §1165 — the sponsor swag catalogue, read from the webshop.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-01: <i>"a new sponsor swag catalog which can be used to put in attendees
/// bags … with prices, teaser text and picture"</i> · <i>"it must be possible to reserve and order
/// so they become unavailable"</i> · <i>"it must also create invoice if ordered"</i>.</para>
///
/// <para>🔑 <b>The webshop is the catalogue.</b> He chose Woo products over a CEH-native catalogue,
/// and that decision pays for itself three times: the price, picture and teaser are maintained in
/// one place and cannot disagree with what is for sale; <i>ordered ⇒ unavailable</i> is the shop's
/// existing stock, the same mechanism the sponsor session slots already use; and an order already
/// raises an e-conomic draft invoice through the webhooks worker, so <i>"create invoice if
/// ordered"</i> needed no code at all.</para>
///
/// <para>🔴 <b>An empty catalogue is never silent.</b> §767 shipped a SharePoint sweep whose matcher
/// was written from a guessed naming convention; it matched nothing for four production runs and
/// nobody could see it, because "found nothing" and "there is nothing" read identically. A category
/// needle has exactly that failure mode, so a configured category matching no product returns a
/// DIAGNOSTIC rather than an empty list.</para>
/// </remarks>
public sealed class SponsorSwagCatalogService
{
    private readonly WooCommerceClient _woo;
    private readonly SponsorConfigLoader _configLoader;
    private readonly SponsorConfigOptions _configOptions;
    private readonly EventEditionConfigLoader? _eventConfigLoader;
    private readonly EventConfigOptions? _eventConfigOptions;
    private readonly ILogger<SponsorSwagCatalogService> _log;

    public SponsorSwagCatalogService(
        WooCommerceClient woo,
        SponsorConfigLoader configLoader,
        SponsorConfigOptions configOptions,
        ILogger<SponsorSwagCatalogService> log,
        // Optional so existing constructions and tests keep working; without them the bag quantity
        // simply has no attendee-count fallback, which the caller can see because it is 0.
        EventEditionConfigLoader? eventConfigLoader = null,
        EventConfigOptions? eventConfigOptions = null)
    {
        _woo = woo;
        _configLoader = configLoader;
        _configOptions = configOptions;
        _log = log;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
    }

    /// <summary>
    /// How many pieces one bag order is: the configured bag quantity, else the edition's expected
    /// attendee count, else 0 ("we do not know", which the page renders as no total at all).
    /// </summary>
    private int ResolveBagQuantity(SwagCatalogConfig? swag)
    {
        if (swag?.BagQuantity is int q && q > 0) return q;

        if (_eventConfigLoader is not null && _eventConfigOptions is not null)
        {
            try
            {
                var edition = _eventConfigLoader.Load(_eventConfigOptions.EventConfigPath);
                if (edition.ExpectedAttendees > 0) return edition.ExpectedAttendees;
            }
            catch
            {
                // A missing edition config must not take the catalogue down; 0 just means no total.
            }
        }

        return 0;
    }

    /// <summary>Read the catalogue. Never throws for a configuration problem — it reports one.</summary>
    public async Task<SwagCatalogResult> GetAsync(CancellationToken ct = default)
    {
        string? category = null;
        SwagCatalogConfig? swag = null;
        try
        {
            swag = _configLoader.Load(_configOptions.SponsorConfigPath).SwagCatalog;
            category = swag?.CategoryName?.Trim();
        }
        catch (FileNotFoundException)
        {
            return new(Array.Empty<SwagCatalogItem>(), null,
                "The sponsor config could not be read, so the swag catalogue cannot be resolved.");
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            return new(Array.Empty<SwagCatalogItem>(), null,
                "No swag-catalogue category is configured (sponsor.<edition>.json → "
                + "swagCatalog.categoryName), so no products can be identified as catalogue items.");
        }

        var quantity = ResolveBagQuantity(swag);
        var currency = string.IsNullOrWhiteSpace(swag?.CurrencySymbol) ? null : swag!.CurrencySymbol!.Trim();

        IReadOnlyList<WooCommerceClient.WooCatalogProduct> products;
        try
        {
            products = await _woo.GetAllProductsAsync(ct);
        }
        catch (Exception ex)
        {
            // ⚠️ A failed read is NOT an empty catalogue. Returning an empty list here would tell a
            // sponsor "there is nothing to buy" because the shop was briefly unreachable.
            _log.LogWarning(ex, "§1165: could not read webshop products for the swag catalogue.");
            return new(Array.Empty<SwagCatalogItem>(), category,
                "The webshop could not be reached, so the catalogue is unavailable right now. "
                + "This is not the same as the catalogue being empty.",
                quantity, currency);
        }

        var items = products
            // 🔴 §1165n — PUBLISHED ONLY. The products endpoint's default status filter is "any",
            // and the live shop carries 44 drafts and 2 private products (measured 2026-09-02). A
            // draft is a product somebody has not finished writing; showing one to a sponsor is
            // showing them a work in progress, and a private one is deliberately not for them.
            //
            // 🔑 Status, NOT catalog visibility. A product hidden from the shop listing is still a
            // real, finished, buyable product — and CEH is a DIFFERENT surface from the shop front,
            // deliberately so. Filtering on visibility would hide exactly the items meant to be
            // sold through the hub rather than browsed in the shop.
            .Where(p => string.Equals(p.Status, "publish", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Categories.Any(c =>
                c.Contains(category!, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p =>
            {
                // §1165 — "where they see unit price and total price x1500".
                //
                // ⚠️ Parsed INVARIANT. WooCommerce sends "49.00" with a dot whatever the shop's
                // display locale, and this server runs in a Danish culture where the dot is a
                // thousands separator — parsing it culturally would read 49.00 as four thousand
                // nine hundred, and then multiply that by the bag quantity. A price wrong by 100×
                // on a page a sponsor buys from is not a rounding bug.
                decimal? unit = decimal.TryParse(
                    p.PriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

                return new SwagCatalogItem(
                    ProductId: p.Id,
                    Name: p.Name,
                    TeaserHtml: p.TeaserHtml,
                    PriceText: p.PriceText,
                    ImageUrl: p.ImageUrl,
                    BuyUrl: p.PermalinkUrl,
                    Available: p.InStock,
                    RemainingCount: p.StockQuantity,
                    UnitPrice: unit,
                    // 🔒 Null when the unit price could not be read — NOT zero. "Total: 0" on a
                    // catalogue page reads as "free", which is the one wrong answer a sponsor might
                    // act on.
                    TotalPrice: unit is null ? null : unit * quantity);
            })
            .ToList();

        if (items.Count == 0)
        {
            // 🔴 The §767 guard: a needle that matches nothing must SAY so.
            var diagnostic =
                $"No webshop product is in the category '{category}', so the swag catalogue is "
                + $"empty. {products.Count} product(s) were read, so the shop answered — check the "
                + "category name in sponsor config against the webshop, and that the products are "
                + "published.";
            _log.LogWarning("§1165: {Diagnostic}", diagnostic);
            return new(items, category, diagnostic, quantity, currency);
        }

        return new(items, category, null, quantity, currency);
    }
}
