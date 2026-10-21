using System.Globalization;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1165 — the sponsor swag catalogue. 🔴 §1312 (operator 2026-10-01: "we dont want to use woocommerce anymore. ceh ows the
/// webshop functionality 100%"): it is read from CEH's OWN shop products of the active edition — CEH's price, CEH's stock,
/// CEH's picture and the CEH shop's product page — no longer from the webshop.
///
/// <para>Operator 2026-09-01: <i>"a new sponsor swag catalog which can be used to put in attendees
/// bags … with prices, teaser text and picture"</i> · <i>"it must be possible to reserve and order so
/// they become unavailable"</i>.</para>
///
/// <para>The interesting cases are all the ways an empty catalogue can mean something different. NO real customer names.</para>
/// </summary>
public sealed class SponsorSwagCatalogTests
{
    private const string CatalogCategory = "Attendee Bag Catalog";

    private sealed record Seeded(CommunityHubDbContext Db, int EventId);

    private static async Task<Seeded> NewDbAsync()
    {
        var db = ScenarioFixture.NewDb();
        var ev = new Event { CommunityName = "Demo", Code = $"T{Guid.NewGuid():N}"[..8], DisplayName = "Demo", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return new(db, ev.Id);
    }

    /// <summary>One CEH shop product in the given category, as an organizer would have it in the shop.</summary>
    private static async Task<ShopProduct> ProductAsync(
        Seeded s, long? wooId, string name, string category = CatalogCategory,
        decimal price = 49m, decimal? salePrice = null, bool manage = false, int? qty = null, bool inStock = true,
        ShopProductStatus status = ShopProductStatus.Published, bool withPicture = true, int? eventId = null)
    {
        var ev = eventId ?? s.EventId;
        var cat = s.Db.ShopCategories.Local.FirstOrDefault(c => c.EventId == ev && c.Name == category)
                  ?? s.Db.ShopCategories.Add(new ShopCategory { EventId = ev, Name = category, Group = ShopGroup.Swag }).Entity;
        var p = new ShopProduct
        {
            EventId = ev, WooProductId = wooId, Name = name, ShortDescriptionHtml = "<p>A nice thing.</p>",
            PriceEur = price, SalePriceEur = salePrice, Status = status, ManageStock = manage,
            StockQuantity = manage ? qty : null, InStock = inStock, PrimaryCategory = cat,
        };
        p.Categories.Add(new ShopProductCategory { Product = p, Category = cat });
        if (withPicture) p.Images.Add(new ShopProductImage { SortOrder = 0, SourceUrl = ShopProductImage.CehUploadPrefix + "x" });
        s.Db.ShopProducts.Add(p);
        await s.Db.SaveChangesAsync();
        return p;
    }

    /// <summary>
    /// Builds the service over the CEH database and a REAL sponsor-config file.
    /// </summary>
    /// <remarks>
    /// 🔑 A temp file rather than a stubbed loader, deliberately: this way the test also proves that
    /// <c>swagCatalog.categoryName</c> actually BINDS from JSON. A stub would have passed happily
    /// with a mis-spelled property name, and "the config silently did not bind" is the same class of
    /// invisible failure the diagnostic below exists to prevent.
    /// </remarks>
    private static SponsorSwagCatalogService New(CommunityHubDbContext db, string? categoryName = CatalogCategory, int? bagQuantity = null)
    {
        var qty = bagQuantity is null ? string.Empty : $", \"bagQuantity\": {bagQuantity}";
        var json = categoryName is null
            ? "{ \"_schema\": \"test\" }"
            : $"{{ \"swagCatalog\": {{ \"categoryName\": \"{categoryName}\"{qty}, \"currencySymbol\": \"€\" }} }}";
        var path = Path.Combine(Path.GetTempPath(), $"swag-cat-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        return new SponsorSwagCatalogService(
            db, new SponsorConfigLoader(), new SponsorConfigOptions { SponsorConfigPath = path },
            NullLogger<SponsorSwagCatalogService>.Instance);
    }

    /// <summary>
    /// 🔴 §1312 — the catalogue lists CEH's published products of the configured swag category, with CEH's price, CEH's
    /// picture and the CEH shop's own buy link. The id stays the old webshop id, so existing swag reservations keep matching.
    /// </summary>
    [Fact]
    public async Task A_CEH_product_in_the_catalog_category_becomes_an_item_with_CEHs_price_stock_picture_and_buy_link()
    {
        var s = await NewDbAsync();
        var p = await ProductAsync(s, 10, "Branded Bottle", manage: true, qty: 4);

        var r = await New(s.Db).GetAsync();

        var item = Assert.Single(r.Items);
        Assert.Equal(10, item.ProductId);
        Assert.Equal("Branded Bottle", item.Name);
        Assert.Contains("A nice thing", item.TeaserHtml);
        Assert.Equal("49.00", item.PriceText);
        Assert.Equal($"/shop-image/{p.Images.Single().Id}", item.ImageUrl);
        Assert.Equal($"/Sponsor/Shop/Product?id={p.Id}", item.BuyUrl);
        Assert.DoesNotContain("shop.example", item.BuyUrl);
        Assert.True(item.Available);
        Assert.Equal(4, item.RemainingCount);
        Assert.Null(r.Diagnostic);
    }

    [Fact]
    public async Task A_discounted_product_is_listed_at_its_sale_price()
    {
        var s = await NewDbAsync();
        await ProductAsync(s, 11, "Branded Tote", price: 20m, salePrice: 15m);

        var item = Assert.Single((await New(s.Db).GetAsync()).Items);

        Assert.Equal("15.00", item.PriceText);
        Assert.Equal(15m, item.UnitPrice);
    }

    /// <summary>A product created in CEH (no old webshop id) is listed under a negative id that cannot collide with one.</summary>
    [Fact]
    public async Task A_product_created_in_CEH_has_a_negative_id_and_no_picture_means_no_image()
    {
        var s = await NewDbAsync();
        var p = await ProductAsync(s, null, "Branded Lanyard", withPicture: false);

        var item = Assert.Single((await New(s.Db).GetAsync()).Items);

        Assert.Equal(-p.Id, item.ProductId);
        Assert.Null(item.ImageUrl);
    }

    [Fact]
    public async Task Only_the_active_editions_products_are_in_the_catalog()
    {
        var s = await NewDbAsync();
        var old = new Event { CommunityName = "Demo", Code = $"O{Guid.NewGuid():N}"[..8], DisplayName = "Old", IsActive = false };
        s.Db.Events.Add(old);
        await s.Db.SaveChangesAsync();
        await ProductAsync(s, 12, "Last year's Cap", eventId: old.Id);
        await ProductAsync(s, 13, "This year's Cap");

        Assert.Equal(new[] { "This year's Cap" }, (await New(s.Db).GetAsync()).Items.Select(i => i.Name));
    }

    [Fact]
    public async Task A_product_outside_the_category_is_not_in_the_catalog()
    {
        var s = await NewDbAsync();
        await ProductAsync(s, 11, "Gold Exhibitor Booth", category: "Exhibitor Tier Package with Booth");

        var r = await New(s.Db).GetAsync();

        Assert.Empty(r.Items);
        // ⚠️ Empty, but for a KNOWN reason — products were read and nothing matched.
        Assert.NotNull(r.Diagnostic);
    }

    /// <summary>
    /// 🔑 <i>"ordered ⇒ unavailable"</i> — CEH's own stock (§1312: taken at reservation, given back on cancel).
    /// </summary>
    [Fact]
    public async Task An_out_of_stock_item_is_listed_but_not_available()
    {
        var s = await NewDbAsync();
        await ProductAsync(s, 12, "Branded Socks", inStock: false);

        var r = await New(s.Db).GetAsync();

        var item = Assert.Single(r.Items);
        Assert.False(item.Available);
        // 🔒 Still LISTED. A sponsor seeing "taken" learns something; an item that silently vanishes
        // just looks like it never existed.
        Assert.Empty(r.AvailableItems);
    }

    /// <summary>A counted product whose last unit is taken is not available, even if nobody flipped its in-stock flag.</summary>
    [Fact]
    public async Task A_counted_product_at_zero_is_not_available()
    {
        var s = await NewDbAsync();
        await ProductAsync(s, 13, "Branded Cap", manage: true, qty: 0, inStock: true);

        var item = Assert.Single((await New(s.Db).GetAsync()).Items);

        Assert.False(item.Available);
        Assert.Equal(0, item.RemainingCount);
    }

    /// <summary>
    /// ⚠️ No tracked count is UNKNOWN, not zero.
    /// </summary>
    [Fact]
    public async Task A_missing_stock_count_is_null_rather_than_zero()
    {
        var s = await NewDbAsync();
        await ProductAsync(s, 14, "Branded Pen");

        var item = Assert.Single((await New(s.Db).GetAsync()).Items);
        Assert.Null(item.RemainingCount);
        Assert.True(item.Available);   // in stock, just not counted
    }

    [Fact]
    public async Task A_tracked_stock_count_is_carried_through()
    {
        var s = await NewDbAsync();
        await ProductAsync(s, 15, "Branded Mug", manage: true, qty: 3);

        Assert.Equal(3, Assert.Single((await New(s.Db).GetAsync()).Items).RemainingCount);
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
        var s = await NewDbAsync();
        await ProductAsync(s, 16, "Something Else", category: "Booth Furniture");

        var r = await New(s.Db).GetAsync();

        Assert.Empty(r.Items);
        Assert.Contains(CatalogCategory, r.Diagnostic);
        Assert.Contains("1 product(s) were read", r.Diagnostic);
    }

    [Fact]
    public async Task No_configured_category_is_reported_as_a_configuration_gap()
    {
        var s = await NewDbAsync();

        var r = await New(s.Db, categoryName: null).GetAsync();

        Assert.Null(r.ConfiguredCategory);
        Assert.Contains("swagCatalog.categoryName", r.Diagnostic);
    }

    /// <summary>
    /// 🔴 A catalogue that cannot be read is NOT an empty catalogue.
    /// </summary>
    /// <remarks>
    /// Returning an empty list on a failed read would tell a sponsor "there is nothing to buy" because the read
    /// briefly failed — the same "an empty read is indistinguishable from a real empty" trap the order pull guards against.
    /// </remarks>
    [Fact]
    public async Task A_read_failure_is_reported_and_never_looks_like_an_empty_catalog()
    {
        var s = await NewDbAsync();
        var svc = New(s.Db);
        await s.Db.DisposeAsync();   // the read now throws

        var r = await svc.GetAsync();

        Assert.Empty(r.Items);
        Assert.Contains("not the same as the catalogue being empty", r.Diagnostic);
    }

    /// <summary>
    /// §1165 — <i>"where they see unit price and total price x1500"</i>.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>The price text is INVARIANT, and that is the whole test.</b> This server runs in a Danish culture where the dot is
    /// a THOUSANDS separator. A cultural format/parse round trip would turn 49.00 into 4,900 and then multiply it by the bag
    /// quantity — a price wrong by 100× on the page a sponsor buys from.
    /// </remarks>
    [Fact]
    public async Task Unit_and_total_price_are_parsed_invariantly_and_multiplied()
    {
        var s = await NewDbAsync();
        await ProductAsync(s, 30, "Branded Power Bank");
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("da-DK");
        try
        {
            var item = Assert.Single((await New(s.Db, bagQuantity: 1500).GetAsync()).Items);

            Assert.Equal("49.00", item.PriceText);
            Assert.Equal(49.00m, item.UnitPrice);
            Assert.Equal(73_500m, item.TotalPrice);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public async Task The_bag_quantity_and_currency_come_from_config()
    {
        var s = await NewDbAsync();
        await ProductAsync(s, 32, "Branded Cap");

        var r = await New(s.Db, bagQuantity: 1500).GetAsync();

        Assert.Equal(1500, r.BagQuantity);
        Assert.Equal("€", r.CurrencySymbol);
    }

    /// <summary>
    /// 🔴 §1165n — a DRAFT is not in the catalogue.
    /// </summary>
    /// <remarks>
    /// A draft is something somebody has not finished writing, and showing one to a sponsor is showing them a work in
    /// progress with a price on it; a private one (TEST/DEV products) is deliberately not for them; a removed one is gone.
    /// </remarks>
    [Theory]
    [InlineData(ShopProductStatus.Draft)]
    [InlineData(ShopProductStatus.Private)]
    [InlineData(ShopProductStatus.Removed)]
    public async Task An_unpublished_product_is_not_in_the_catalog(ShopProductStatus status)
    {
        var s = await NewDbAsync();
        await ProductAsync(s, 40, "Half-written Item", status: status);

        var r = await New(s.Db).GetAsync();

        Assert.Empty(r.Items);
        // Empty for a KNOWN reason — products were read and nothing publishable matched.
        Assert.NotNull(r.Diagnostic);
    }

    [Fact]
    public async Task A_published_product_is_in_the_catalog()
    {
        var s = await NewDbAsync();
        await ProductAsync(s, 41, "Finished Item");

        Assert.Single((await New(s.Db).GetAsync()).Items);
    }

    [Fact]
    public async Task Items_come_back_in_a_stable_order()
    {
        var s = await NewDbAsync();
        await ProductAsync(s, 20, "Zeta Bottle");
        await ProductAsync(s, 21, "Alpha Cap");

        var r = await New(s.Db).GetAsync();

        Assert.Equal(new[] { "Alpha Cap", "Zeta Bottle" }, r.Items.Select(i => i.Name));
    }
}
