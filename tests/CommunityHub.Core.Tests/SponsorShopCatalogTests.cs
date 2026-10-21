using System.Net;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1305 — the CEH sponsor shop's catalogue: names, groups, availability, and the webshop client's
/// stock writes. §1312 (2026-10-01): the import from the webshop is gone and CEH is the one stock
/// counter, so availability is CEH's own count.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-26: <i>"take the products from the current Webshop – same text, same
/// pictures, same prices, same availability"</i> · <i>"ceh and woocommerce must have same
/// availability"</i> · <i>"go ahead with live stock, sync every 2 min. two-way"</i>.</para>
///
/// <para>The product and category names below are shaped like the live shop's (measured 2026-09-26),
/// because the booth and the timeslot exist ONLY in the name. NO real customer names.</para>
/// </remarks>
public sealed class SponsorShopCatalogTests
{
    // ---------------------------------------------------------------------------------------------
    // Reading the booth and the slot out of a name
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Platinum Exhibitor ELDK27 - Booth E-1", "E-1")]
    [InlineData("Gold Exhibitor ELDK27 - Booth E-20A (Shared)", "E-20A")]
    [InlineData("Appreciation Sponsor ELDK27 with Exhibitor Booth E-24 (feature size)", "E-24")]
    [InlineData("Gold Exhibitor ELDK27 - Booth e-30 (in lounge)", "E-30")]
    [InlineData("Booth: Bar stool, black. Hay - About a stool (W45 D48 H85)", null)]
    [InlineData("Coffee Sponsor ELDK27", null)]
    public void The_booth_is_read_from_the_name_or_not_at_all(string name, string? expected) =>
        Assert.Equal(expected, ShopProductNameParser.BoothId(name));

    [Theory]
    [InlineData("Sponsor Long 60 min Session - ELDK27 - timeslot: 2 (est. time: 11:30-12:30)", "2", "11:30-12:30")]
    [InlineData("Sponsor Morning 50 min Session - ELDK27 - timeslot: early (est. time: 07:20-08:10)", "early", "07:20-08:10")]
    [InlineData("Sponsor Expo Intro 20 min Session - ELDK27 - timeslot: 0 (TEST)", "0", null)]
    [InlineData("Pre-day: AI Master Class Track Sponsor", null, null)]
    public void The_timeslot_and_its_time_are_read_from_the_name(string name, string? slot, string? time)
    {
        var (s, t) = ShopProductNameParser.Timeslot(name);
        Assert.Equal(slot, s);
        Assert.Equal(time, t);
    }

    // ---------------------------------------------------------------------------------------------
    // Grouping the webshop's categories into the doors a sponsor walks through
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Exhibitor Tier Package with Booth", ShopGroup.Booths)]
    [InlineData("Platinum Exhibitor", ShopGroup.Booths)]
    [InlineData("Gold Exhibitor (Shared)", ShopGroup.Booths)]
    [InlineData("Exhibitor Session Sponsors", ShopGroup.Sessions)]
    [InlineData("Track Sponsors", ShopGroup.Sessions)]
    [InlineData("Session Preday Lunch (30 min)", ShopGroup.Sessions)]
    [InlineData("Booth Option", ShopGroup.BoothOptions)]
    [InlineData("Booth Furniture", ShopGroup.Furniture)]
    [InlineData("TV Rental", ShopGroup.BoothOptions)]
    [InlineData("Attendee Bag Packaging", ShopGroup.BoothOptions)]
    [InlineData("Package Handling", ShopGroup.PackageHandling)]
    [InlineData("Swags", ShopGroup.Swag)]
    [InlineData("Ticket Packages", ShopGroup.Tickets)]
    [InlineData("Sponsor Tier Package (Digital Only)", ShopGroup.Packages)]
    [InlineData("Silver Sponsor", ShopGroup.Packages)]
    [InlineData("Founding Partner Sponsor", ShopGroup.OrganizerOnly)]
    [InlineData("Swag Sponsor", ShopGroup.OrganizerOnly)]
    [InlineData("Hospitality & Comfort Sponsor", ShopGroup.Branding)]
    [InlineData("Branded Feature Package Sponsor", ShopGroup.Branding)]
    [InlineData("Uncategorized", ShopGroup.Unassigned)]
    public void A_webshop_category_name_maps_to_its_sponsor_facing_group(string name, ShopGroup expected) =>
        Assert.Equal(expected, ShopGroupMapper.FromName(name));

    /// <summary>
    /// 🔴 "Extra Staff Tickets" says "ticket" but is a BOOTH OPTION. The first draft of the mapper put
    /// it in Tickets because the ticket rule ran first.
    /// </summary>
    [Fact]
    public void Extra_staff_tickets_are_a_booth_option_not_a_ticket_pack() =>
        Assert.Equal(ShopGroup.BoothOptions, ShopGroupMapper.FromName("Extra Staff Tickets"));

    [Fact]
    public void A_child_with_an_unrecognised_name_inherits_its_parents_group()
    {
        var names = new Dictionary<long, string> { [1] = "Exhibitor Tier Package with Booth", [2] = "Something new" };
        var parents = new Dictionary<long, long> { [1] = 0, [2] = 1 };
        Assert.Equal(ShopGroup.Booths, ShopGroupMapper.Resolve(2, names, parents));
    }

    /// <summary>
    /// Operator 2026-09-26: "the voucher idea dropped … the tasks can point to the new webshop and list all furnitures
    /// automatically". The furniture task in the CEH shop: allowance as an automatic discount, no coupon, a button into
    /// the furniture tab, every furniture product with its price (invariant numbers — the server runs Danish).
    /// </summary>
    [Fact]
    public void The_furniture_task_lists_the_furniture_and_the_allowance_without_a_coupon()
    {
        var text = CommunityHub.Core.Tasks.SponsorTaskPlaceholderBuilder.CehFurnitureSpec(239m,
            new[] { ("Booth: Chair, black", 35m), ("Booth: Table, white", 29.5m) });

        Assert.Contains("**239 EURO**", text);
        Assert.Contains("no coupon code", text);
        Assert.Contains("(/Sponsor/Shop?Group=Furniture)", text);
        Assert.Contains("* Booth: Chair, black – EURO 35", text);
        Assert.Contains("* Booth: Table, white – EURO 29.5", text);
        Assert.DoesNotContain("{{couponCode}}", text);
    }

    /// <summary>
    /// §1305 item 11 — AI booth detection: the model's answer is read tolerantly (fences, swapped corners, unknown ids
    /// dropped), and each rough rectangle is snapped onto the dashed outline drawn in the picture.
    /// </summary>
    [Fact]
    public void Detected_booths_are_parsed_and_snapped_to_the_drawn_outline()
    {
        var answer = "```json\n{\"booths\":[{\"id\":\"e-17\",\"x1\":107,\"y1\":55,\"x2\":38,\"y2\":16},"
                     + "{\"id\":\"E-99\",\"x1\":1,\"y1\":1,\"x2\":20,\"y2\":20},{\"id\":\"E-18\",\"x1\":\"120\",\"y1\":10,\"x2\":150,\"y2\":50}]}\n```";
        var parsed = ShopBoothDetector.ParseAnswer(answer, new[] { "E-17", "E-18" }, 200, 100);
        Assert.Equal(new[] { "E-17", "E-18" }, parsed.Select(b => b.BoothId));
        Assert.Equal((38, 16, 107, 55), (parsed[0].X1, parsed[0].Y1, parsed[0].X2, parsed[0].Y2));   // corners swapped back

        // A 200×100 picture with a dashed rectangle from (40,20) to (110,60).
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(200, 100, new SixLabors.ImageSharp.PixelFormats.Rgba32(255, 255, 255));
        var black = new SixLabors.ImageSharp.PixelFormats.Rgba32(0, 0, 0);
        for (var x = 40; x <= 110; x++) if (x % 6 < 4) { img[x, 20] = black; img[x, 60] = black; }
        for (var y = 20; y <= 60; y++) if (y % 6 < 4) { img[40, y] = black; img[110, y] = black; }

        var snapped = ShopBoothDetector.Snap(parsed[0], ShopBoothDetector.DarkMap(img), 200, 100);
        Assert.True(snapped.Snapped);
        Assert.Equal((40, 20, 110, 60), (snapped.X1, snapped.Y1, snapped.X2, snapped.Y2));

        var nothingDrawn = ShopBoothDetector.Snap(parsed[1], ShopBoothDetector.DarkMap(img), 200, 100);
        Assert.False(nothingDrawn.Snapped);                                                          // kept as the model said
    }

    // ---------------------------------------------------------------------------------------------
    // Availability — §1312: CEH is the ONE stock counter; the old webshop is closed and never read
    // ---------------------------------------------------------------------------------------------

    private static async Task<List<ShopProduct>> SeedProductsAsync(CommunityHubDbContext db, int eventId)
    {
        ShopProduct P(long woo, string name, ShopProductStatus status, bool manage, int? qty) => new()
        {
            EventId = eventId, WooProductId = woo, Name = name, Slug = $"product-{woo}", PriceEur = 100m,
            Status = status, ManageStock = manage, StockQuantity = manage ? qty : null,
            InStock = !manage || qty is > 0, CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        };
        var products = new List<ShopProduct>
        {
            P(10602, "Gold Exhibitor ELDK27 - Booth E-12", ShopProductStatus.Published, true, 1),
            P(10815, "Gold Exhibitor ELDK27 - Booth E-20A (Shared)", ShopProductStatus.Published, true, 0),
            P(10654, "Booth: Bar stool, black (W45 D48 H85)", ShopProductStatus.Published, false, null),
            P(10741, "Info Feature Exhibitor ELDK27 - Booth E-29 [DEV]", ShopProductStatus.Private, true, 5),
        };
        db.ShopProducts.AddRange(products);
        await db.SaveChangesAsync();
        return products;
    }

    /// <summary>
    /// §1312 — availability is CEH's own count: a counted product with units left is available, one at 0 is
    /// not, an unlimited one always is, and every answer is live (there is no other copy to fall back to).
    /// </summary>
    [Fact]
    public async Task Availability_is_answered_from_CEHs_own_stock_count()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var products = await SeedProductsAsync(db, seed.EventId);

        var live = await new ShopStockService(ScenarioFixture.Clock).GetAsync(products);

        var booth = live[products.Single(p => p.WooProductId == 10602).Id];
        Assert.True(booth.Available);
        Assert.Equal(1, booth.Remaining);
        Assert.True(booth.Live);

        var soldOut = live[products.Single(p => p.WooProductId == 10815).Id];
        Assert.False(soldOut.Available);
        Assert.Equal(0, soldOut.Remaining);

        var stool = live[products.Single(p => p.WooProductId == 10654).Id];
        Assert.True(stool.Available);
        Assert.True(stool.Unlimited);
        Assert.Null(stool.Remaining);
        Assert.All(live.Values, a => Assert.True(a.Live));
    }

    [Fact]
    public async Task A_private_product_is_never_available_even_with_stock()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var products = await SeedProductsAsync(db, seed.EventId);

        var live = await new ShopStockService(ScenarioFixture.Clock).GetAsync(products);

        Assert.False(live[products.Single(p => p.WooProductId == 10741).Id].Available);
    }

    /// <summary>A product an organizer marked out of stock is not available, whatever its count says.</summary>
    [Fact]
    public async Task A_product_marked_out_of_stock_is_not_available()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var products = await SeedProductsAsync(db, seed.EventId);
        var booth = products.Single(p => p.WooProductId == 10602);
        booth.InStock = false;

        var live = await new ShopStockService(ScenarioFixture.Clock).GetAsync(products);

        Assert.False(live[booth.Id].Available);
    }

    /// <summary>
    /// §1312 — product pictures keep their stable SharePoint file name now that the import is gone: the old
    /// webshop's id for a product that came from it, <c>ceh{id}</c> for one created in CEH.
    /// </summary>
    [Fact]
    public void A_product_picture_has_a_stable_readable_file_name()
    {
        var imported = new ShopProduct { Id = 7, WooProductId = 10602, Slug = "product-10602" };
        var cehMade = new ShopProduct { Id = 42, Slug = "gold booth/e-12" };

        Assert.Equal("10602-product-10602-1.png",
            ShopProductPictures.FileName(imported, new ShopProductImage { SortOrder = 0 }, "image/png"));
        Assert.Equal("ceh42-goldboothe-12-3.jpg",
            ShopProductPictures.FileName(cehMade, new ShopProductImage { SortOrder = 2 }, "image/jpeg"));
    }
}
