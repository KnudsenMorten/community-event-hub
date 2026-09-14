using System.Net;
using System.Text;
using CommunityHub.Core.Config;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1165 — the sponsor swag catalogue, read from the webshop.
///
/// <para>Operator 2026-09-01: <i>"a new sponsor swag catalog which can be used to put in attendees
/// bags … with prices, teaser text and picture"</i> · <i>"it must be possible to reserve and order so
/// they become unavailable"</i>.</para>
///
/// <para>🔑 <b>The webshop IS the catalogue</b>, by his decision — so most of these tests are about
/// reading it honestly rather than about logic we own. The interesting cases are all the ways an
/// empty catalogue can mean something different.</para>
///
/// <para>NO real customer names.</para>
/// </summary>
public sealed class SponsorSwagCatalogTests
{
    private const string CatalogCategory = "Attendee Bag Catalog";

    private sealed class FakeShop : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _status;
        public int Calls { get; private set; }

        public FakeShop(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _json = json;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            // Page 2 is empty, so the reader stops after one page.
            var body = Calls == 1 ? _json : "[]";
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// Builds the service over a fake shop and a REAL sponsor-config file.
    /// </summary>
    /// <remarks>
    /// 🔑 A temp file rather than a stubbed loader, deliberately: this way the test also proves that
    /// <c>swagCatalog.categoryName</c> actually BINDS from JSON. A stub would have passed happily
    /// with a mis-spelled property name, and "the config silently did not bind" is the same class of
    /// invisible failure the diagnostic below exists to prevent.
    /// </remarks>
    private static SponsorSwagCatalogService New(
        string productsJson,
        string? categoryName = CatalogCategory,
        HttpStatusCode status = HttpStatusCode.OK,
        int? bagQuantity = null)
    {
        var http = new HttpClient(new FakeShop(productsJson, status))
        {
            BaseAddress = new Uri("https://shop.example.test"),
        };
        var woo = new WooCommerceClient(http, new WooCommerceOptions
        {
            Enabled = true,
            BaseUrl = "https://shop.example.test",
            ConsumerKey = "k",
            ConsumerSecret = "s",
        });

        var qty = bagQuantity is null ? string.Empty : $", \"bagQuantity\": {bagQuantity}";
        var json = categoryName is null
            ? "{ \"_schema\": \"test\" }"
            : $"{{ \"swagCatalog\": {{ \"categoryName\": \"{categoryName}\"{qty}, \"currencySymbol\": \"€\" }} }}";
        var path = Path.Combine(Path.GetTempPath(), $"swag-cat-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        return new SponsorSwagCatalogService(
            woo, new SponsorConfigLoader(), new SponsorConfigOptions { SponsorConfigPath = path },
            NullLogger<SponsorSwagCatalogService>.Instance);
    }

    private static string Product(
        long id, string name, string category,
        string stockStatus = "instock", string? stockQty = null, string price = "49.00",
        string status = "publish") =>
        $$"""
        {
          "id": {{id}},
          "name": "{{name}}",
          "short_description": "<p>A nice thing.</p>",
          "price": "{{price}}",
          "permalink": "https://shop.example.test/product/{{id}}",
          "status": "{{status}}",
          "stock_status": "{{stockStatus}}",
          {{(stockQty is null ? "" : $"\"stock_quantity\": {stockQty},")}}
          "images": [ { "src": "https://shop.example.test/img/{{id}}.png" } ],
          "categories": [ { "name": "{{category}}" } ]
        }
        """;

    [Fact]
    public async Task A_product_in_the_catalog_category_becomes_an_item_with_everything_a_sponsor_needs()
    {
        var svc = New($"[{Product(10, "Branded Bottle", CatalogCategory)}]");

        var r = await svc.GetAsync();

        var item = Assert.Single(r.Items);
        Assert.Equal(10, item.ProductId);
        Assert.Equal("Branded Bottle", item.Name);
        Assert.Contains("A nice thing", item.TeaserHtml);
        Assert.Equal("49.00", item.PriceText);
        Assert.Equal("https://shop.example.test/img/10.png", item.ImageUrl);
        Assert.Equal("https://shop.example.test/product/10", item.BuyUrl);
        Assert.True(item.Available);
        Assert.Null(r.Diagnostic);
    }

    [Fact]
    public async Task A_product_outside_the_category_is_not_in_the_catalog()
    {
        var svc = New($"[{Product(11, "Gold Exhibitor Booth", "Exhibitor Tier Package with Booth")}]");

        var r = await svc.GetAsync();

        Assert.Empty(r.Items);
        // ⚠️ Empty, but for a KNOWN reason — the shop answered and nothing matched.
        Assert.NotNull(r.Diagnostic);
    }

    /// <summary>
    /// 🔑 <i>"ordered ⇒ unavailable"</i> — the shop's own stock, which is why this needed no new
    /// mechanism.
    /// </summary>
    [Fact]
    public async Task An_out_of_stock_item_is_listed_but_not_available()
    {
        var svc = New($"[{Product(12, "Branded Socks", CatalogCategory, stockStatus: "outofstock")}]");

        var r = await svc.GetAsync();

        var item = Assert.Single(r.Items);
        Assert.False(item.Available);
        // 🔒 Still LISTED. A sponsor seeing "taken" learns something; an item that silently vanishes
        // just looks like it never existed.
        Assert.Empty(r.AvailableItems);
    }

    /// <summary>
    /// ⚠️ An unrecognised stock status is NOT available.
    /// </summary>
    /// <remarks>
    /// "onbackorder" and anything Woo adds later must fail toward unavailable: offering an item we
    /// cannot confirm is sellable costs a sponsor conversation and possibly a refund, while wrongly
    /// hiding one costs an e-mail.
    /// </remarks>
    [Fact]
    public async Task An_unknown_stock_status_is_treated_as_unavailable()
    {
        var svc = New($"[{Product(13, "Branded Cap", CatalogCategory, stockStatus: "onbackorder")}]");

        var r = await svc.GetAsync();

        Assert.False(Assert.Single(r.Items).Available);
    }

    /// <summary>
    /// ⚠️ No tracked count is UNKNOWN, not zero.
    /// </summary>
    [Fact]
    public async Task A_missing_stock_count_is_null_rather_than_zero()
    {
        var svc = New($"[{Product(14, "Branded Pen", CatalogCategory)}]");

        var item = Assert.Single((await svc.GetAsync()).Items);
        Assert.Null(item.RemainingCount);
        Assert.True(item.Available);   // in stock, just not counted
    }

    [Fact]
    public async Task A_tracked_stock_count_is_carried_through()
    {
        var svc = New($"[{Product(15, "Branded Mug", CatalogCategory, stockQty: "3")}]");

        Assert.Equal(3, Assert.Single((await svc.GetAsync()).Items).RemainingCount);
    }

    /// <summary>
    /// 🔴 §767's lesson: a needle that matches nothing must SAY so.
    /// </summary>
    /// <remarks>
    /// §767 shipped a SharePoint sweep whose matcher was written from a guessed convention. It
    /// matched nothing for four production runs and was invisible, because "found nothing" and
    /// "there is nothing" read identically. The diagnostic names the category and the number of
    /// products actually read, so the two can be told apart at a glance.
    /// </remarks>
    [Fact]
    public async Task A_category_that_matches_no_product_is_reported_not_silent()
    {
        var svc = New($"[{Product(16, "Something Else", "Booth Furniture")}]");

        var r = await svc.GetAsync();

        Assert.Empty(r.Items);
        Assert.Contains(CatalogCategory, r.Diagnostic);
        Assert.Contains("1 product(s) were read", r.Diagnostic);
    }

    [Fact]
    public async Task No_configured_category_is_reported_as_a_configuration_gap()
    {
        var svc = New("[]", categoryName: null);

        var r = await svc.GetAsync();

        Assert.Null(r.ConfiguredCategory);
        Assert.Contains("swagCatalog.categoryName", r.Diagnostic);
    }

    /// <summary>
    /// 🔴 A shop that cannot be reached is NOT an empty catalogue.
    /// </summary>
    /// <remarks>
    /// Returning an empty list on a failed read would tell a sponsor "there is nothing to buy"
    /// because the webshop was briefly down — the same "an empty read is indistinguishable from a
    /// real empty" trap the order pull guards against before sweeping action items.
    /// </remarks>
    [Fact]
    public async Task A_webshop_failure_is_reported_and_never_looks_like_an_empty_catalog()
    {
        var svc = New("[]", status: HttpStatusCode.InternalServerError);

        var r = await svc.GetAsync();

        Assert.Empty(r.Items);
        Assert.Contains("not the same as the catalogue being empty", r.Diagnostic);
    }

    /// <summary>
    /// §1165 — <i>"where they see unit price and total price x1500"</i>.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>The parse is INVARIANT, and that is the whole test.</b> WooCommerce sends "49.00" with a
    /// dot whatever the shop displays, while this server runs in a Danish culture where the dot is a
    /// THOUSANDS separator. A cultural parse would read 49.00 as 4,900 and then multiply it by the
    /// bag quantity — a price wrong by 100× on the page a sponsor buys from.
    /// </remarks>
    [Fact]
    public async Task Unit_and_total_price_are_parsed_invariantly_and_multiplied()
    {
        var svc = New($"[{Product(30, "Branded Power Bank", CatalogCategory)}]", bagQuantity: 1500);

        var item = Assert.Single((await svc.GetAsync()).Items);

        Assert.Equal(49.00m, item.UnitPrice);
        Assert.Equal(73_500m, item.TotalPrice);
    }

    /// <summary>
    /// 🔒 An unreadable price yields NO total — never zero.
    /// </summary>
    /// <remarks>
    /// "Total: 0" on a catalogue page reads as "free", which is the one wrong answer a sponsor might
    /// actually act on. A missing number must look missing.
    /// </remarks>
    [Fact]
    public async Task An_unreadable_price_produces_no_total_rather_than_zero()
    {
        var svc = New($"[{Product(31, "Mystery Item", CatalogCategory, price: "on request")}]", bagQuantity: 1500);

        var item = Assert.Single((await svc.GetAsync()).Items);

        Assert.Null(item.UnitPrice);
        Assert.Null(item.TotalPrice);
        // The shop's own text survives, so the page can still show something honest.
        Assert.Equal("on request", item.PriceText);
    }

    [Fact]
    public async Task The_bag_quantity_and_currency_come_from_config()
    {
        var svc = New($"[{Product(32, "Branded Cap", CatalogCategory)}]", bagQuantity: 1500);

        var r = await svc.GetAsync();

        Assert.Equal(1500, r.BagQuantity);
        Assert.Equal("€", r.CurrencySymbol);
    }

    /// <summary>
    /// 🔴 §1165n — a DRAFT is not in the catalogue.
    /// </summary>
    /// <remarks>
    /// Measured on the live shop 2026-09-02: the products endpoint returns 44 draft and 2 private
    /// products alongside 54 published ones, because its default status filter is "any". A draft is
    /// something somebody has not finished writing, and showing one to a sponsor is showing them a
    /// work in progress with a price on it.
    /// </remarks>
    [Theory]
    [InlineData("draft")]
    [InlineData("private")]
    [InlineData("pending")]
    public async Task An_unpublished_product_is_not_in_the_catalog(string status)
    {
        var svc = New($"[{Product(40, "Half-written Item", CatalogCategory, status: status)}]");

        var r = await svc.GetAsync();

        Assert.Empty(r.Items);
        // Empty for a KNOWN reason — the shop answered and nothing publishable matched.
        Assert.NotNull(r.Diagnostic);
    }

    [Fact]
    public async Task A_published_product_is_in_the_catalog()
    {
        var svc = New($"[{Product(41, "Finished Item", CatalogCategory, status: "publish")}]");

        Assert.Single((await svc.GetAsync()).Items);
    }

    [Fact]
    public async Task Items_come_back_in_a_stable_order()
    {
        var svc = New($"[{Product(20, "Zeta Bottle", CatalogCategory)},{Product(21, "Alpha Cap", CatalogCategory)}]");

        var r = await svc.GetAsync();

        Assert.Equal(new[] { "Alpha Cap", "Zeta Bottle" }, r.Items.Select(i => i.Name));
    }
}
