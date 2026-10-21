using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1312 — live price and availability in the sponsor catalogue. Operator 2026-09-27: "i am missing prices + availability"
/// · "i also need an availability like in the powerpoint for all reservations / confirmed orders" · "show both available
/// and reserved/confirmed - 2 views". Product names are the real ELDK27 ones.
/// </summary>
public sealed class ShopPackageAvailabilityTests
{
    private static int _id;

    private static ShopProduct Product(string name, string category, ShopGroup group, decimal price, string? booth = null, string? slot = null) => new()
    {
        Id = ++_id, EventId = 1, Name = name, PriceEur = price, BoothId = booth, Timeslot = slot, Status = ShopProductStatus.Published,
        ManageStock = true, StockQuantity = 1, InStock = true,
        PrimaryCategoryId = category.GetHashCode(), PrimaryCategory = new ShopCategory { Id = category.GetHashCode(), Name = category, Group = group },
    };

    [Theory]
    [InlineData("Platinum Exhibitor", "Platinum Exhibitor ELDK27 - Booth E-1", "Platinum Exhibitor", true)]
    [InlineData("Gold Exhibitor", "Gold Exhibitor ELDK27 - Booth E-20A (Shared)", "Gold Exhibitor (Shared)", true)]
    [InlineData("Diamond Exhibitor", "Gold Exhibitor ELDK27 - Booth E-12", "Gold Exhibitor", false)]
    [InlineData("Sponsor Sessions", "Sponsor Long 60 min Session - ELDK27 - timeslot: 1 (est. time: 10:00-11:00)", "Session (60 min)", true)]
    [InlineData("Pre-Day Master Class Sponsor", "Pre-day: Security Master Class Track Sponsor", "Track Sponsors", true)]
    [InlineData("Logo Thermo Bottle & Water Station Sponsor", "Thermo Bottle w/sponsor logo for all attendees & Water Station Sponsor (all rooms, expo, lounge)", "Hospitality & Comfort Sponsor", true)]
    [InlineData("Name Badge Sponsor", "Named Lounge Sponsor TreeHouse ELDK27", "Hospitality & Comfort Sponsor", false)]
    [InlineData("Attendee Bag Sponsor", "Attendee Bag Content Packaging - ELDK27", "Attendee Bag Packaging", false)]
    [InlineData("ELDK26 Sponsors", "Coffee Sponsor ELDK27", "Hospitality & Comfort Sponsor", false)]
    [InlineData("ELDK27", "Coffee Sponsor ELDK27", "Hospitality & Comfort Sponsor", false)]     // an edition code alone matches nothing
    [InlineData("", "Coffee Sponsor ELDK27", "Hospitality & Comfort Sponsor", false)]
    public void A_page_title_picks_its_packages(string match, string productName, string category, bool expected) =>
        Assert.Equal(expected, ShopPackageAvailability.Matches(Product(productName, category, ShopGroup.Branding, 100), match));

    [Theory]
    [InlineData("Platinum Exhibitor ELDK27 - Booth E-1", "Platinum Exhibitor")]
    [InlineData("Sponsor Long 60 min Session - ELDK27 - timeslot: 1 (est. time: 10:00-11:00)", "Sponsor Long 60 min Session")]
    [InlineData("Coffee Sponsor ELDK27", "Coffee Sponsor")]
    [InlineData("Appreciation Sponsor ELDK27 (existing exhibitor with booth)", "Appreciation Sponsor (existing exhibitor with booth)")]
    public void The_package_name_drops_the_edition_and_the_slot(string productName, string label) =>
        Assert.Equal(label, ShopPackageAvailability.PackageLabel(productName));

    [Fact]
    public void Booths_are_one_line_per_tier_with_available_reserved_and_confirmed_counted()
    {
        var e1 = Product("Platinum Exhibitor ELDK27 - Booth E-1", "Platinum Exhibitor", ShopGroup.Booths, 13500, "E-1");
        var e2 = Product("Platinum Exhibitor ELDK27 - Booth E-2", "Platinum Exhibitor", ShopGroup.Booths, 13500, "E-2");
        var e3 = Product("Platinum Exhibitor ELDK27 - Booth E-3", "Platinum Exhibitor", ShopGroup.Booths, 13500, "E-3");
        var e10 = Product("Platinum Exhibitor ELDK27 - Booth E-10", "Platinum Exhibitor", ShopGroup.Booths, 13500, "E-10");
        var shared = Product("Gold Exhibitor ELDK27 - Booth E-20A (Shared)", "Gold Exhibitor (Shared)", ShopGroup.Booths, 3750, "E-20A");
        var shared2 = Product("Gold Exhibitor ELDK27 - Booth E-20B (Shared)", "Gold Exhibitor (Shared)", ShopGroup.Booths, 3750, "E-20B");
        var coffee = Product("Coffee Sponsor ELDK27", "Hospitality & Comfort Sponsor", ShopGroup.Branding, 10000);
        var silver = Product("Silver Sponsor ELDK27", "Sponsor Tier Package (Digital Only)", ShopGroup.Packages, 4000);
        silver.ManageStock = false;

        var now = DateTimeOffset.UtcNow;
        var live = new Dictionary<int, ShopAvailability>
        {
            [e1.Id] = new(false, 0, false, true, now),     // signed in CEH
            [e2.Id] = new(false, 0, false, true, now),     // sold in the old webshop — no CEH order
            [e3.Id] = new(true, 1, false, true, now),      // reserved in CEH, not signed
            [e10.Id] = new(true, 1, false, true, now),
            [shared.Id] = new(true, 1, false, true, now),
            [shared2.Id] = new(true, 1, false, true, now),
            [coffee.Id] = new(false, 0, false, true, now),
            [silver.Id] = new(true, null, true, true, now),
        };
        var held = new[]
        {
            new ShopPackageAvailability.Hold(e1.Id, Signed: true, "c1", 12000m),     // an agreed price, below the list price
            new ShopPackageAvailability.Hold(e3.Id, Signed: false, "c2", 13500m),
            new ShopPackageAvailability.Hold(silver.Id, Signed: true, "c1"),
        };
        var names = new Dictionary<string, string> { ["c1"] = "Acme", ["c2"] = "Beta" };

        var rows = ShopPackageAvailability.BuildRows(new[] { coffee, e10, e3, e2, e1, shared, shared2, silver }, live, held, names);

        // Group order, then by NAME within a group (operator 2026-09-28: "sort order should not be due to price but title").
        Assert.Equal(new[] { "Gold Exhibitor (Shared)", "Platinum Exhibitor", "Silver Sponsor", "Coffee Sponsor" }, rows.Select(r => r.Label));
        var platinum = rows[1];
        Assert.Equal((1, 1, 2, 4), (platinum.Available, platinum.Reserved, platinum.Confirmed, platinum.Maximum!.Value));
        Assert.Equal(new[] { "E-1", "E-2", "E-3" }, platinum.Takers.Select(t => t.Slot));          // E-2 before E-10
        Assert.Equal(new[] { "Acme", null, "Beta" }, platinum.Takers.Select(t => t.Company));
        // The one Availability page colours every booth (operator 2026-09-28: "much easier to communicate with colors").
        Assert.Equal(new[]
        {
            ("E-1", ShopBoothState.Confirmed, (string?)"Acme"), ("E-2", ShopBoothState.Confirmed, null),
            ("E-3", ShopBoothState.Reserved, "Beta"), ("E-10", ShopBoothState.Free, null),
        }, platinum.Slots!.Select(s => (s.Label!, s.State, s.Company)));
        Assert.Equal(new[] { "E-10" }, platinum.FreeSlots);
        Assert.Equal((2, 2), (rows[0].Available, rows[0].Maximum!.Value));
        Assert.True(rows[2].Unlimited);
        Assert.Null(rows[2].Maximum);
        Assert.Equal(1, rows[2].Confirmed);
        Assert.Equal((0, 1), (rows[3].Available, rows[3].Confirmed));                             // sold elsewhere

        // The summary's money (organizers only): the agreed line amount, and the product price for a sale in the old shop.
        Assert.Equal(12000m + 13500m + 13500m, platinum.BookedEur);
        Assert.Equal(12000m + 13500m, platinum.ConfirmedEur);

        var anonymous = ShopPackageAvailability.BuildRows(new[] { e1, e3 }, live, held, companyNames: null);
        Assert.All(anonymous.SelectMany(r => r.Takers), t => Assert.Null(t.Company));              // no names unless asked for
    }

    [Fact]
    public void Every_selling_line_in_the_edition_config_reaches_a_real_package()
    {
        // Operator 2026-09-28: "include the key things about leads under these sessions … make it more selling". A key that
        // matches no product shows nothing, silently — so each one is checked against the real ELDK27 product names.
        var names = new[]
        {
            "Pre-day: Security Master Class Track Sponsor", "Pre-day: Intune Master Class Track Sponsor", "Pre-day: Identity Master Class Track Sponsor",
            "Pre-day: AI Master Class Track Sponsor", "Pre-day: Azure Master Class Track Sponsor",
            "Sponsor Long 60 min Session - ELDK27 - timeslot: 1 (est. time: 10:00-11:00)", "Sponsor Morning 50 min Session - ELDK27 - timeslot: early (est. time: 07:20-08:10)",
            "Sponsor Expo Intro 20 min Session - ELDK27 - timeslot: 1 (est. time: 11:05-11:25)",
            "Appreciation Sponsor ELDK27 (existing exhibitor with booth)", "Attendee Bag Sponsor ELDK27", "Breakfast & Lunch Sponsor ELDK27",
            "Keynote Video Sponsor ELDK27", "Lounge sponsor ELDK27 with Exhibitor Booth E-27 in lounge (feature size)", "Pre-day Party Sponsor",
            "Pre-day Sponsor ELDK27", "Coffee Sponsor ELDK27", "Popcorn Sponsor ELDK27", "Fruit Sponsor ELDK27", "Name Badge Sponsor ELDK27",
            "Keyhanger Sponsor ELDK27", "Logo on Staff + Speaker polo",
            "Thermo Bottle w/sponsor logo for all attendees & Water Station Sponsor (all rooms, expo, lounge)", "Cake & Snack Sponsor ELDK27",
        };
        var products = names.Select(n => Product(n, "Any", ShopGroup.Branding, 1000)).ToList();
        var cfg = new CommunityHub.Core.Config.ShopConfigLoader().Load(Path.Combine(CommunityHub.Core.Tests.Scenario.RepoPaths.RepoRoot(), "config", "shop.eldk27.json"));

        foreach (var key in cfg.PackageHighlights.Keys.Where(k => !k.StartsWith('_')))
            Assert.True(products.Any(p => ShopPackageAvailability.Matches(p, key)), $"'{key}' matches no ELDK27 package");
        // …and the pre-day line reaches only the pre-day package, not the party or the master classes.
        Assert.Equal(new[] { "Pre-day Sponsor ELDK27" }, products.Where(p => ShopPackageAvailability.Matches(p, "=Pre-day Sponsor")).Select(p => p.Name));
    }

    [Fact]
    public void A_booth_sold_in_the_old_webshop_names_its_buyer()
    {
        // Operator 2026-09-28: "i'm missing the sponsor names for confirmed, important for my selling" — booths sold in the
        // old webshop have no CEH order, so they showed without a name.
        var e1 = Product("Platinum Exhibitor ELDK27 - Booth E-1", "Platinum Exhibitor", ShopGroup.Booths, 13500, "E-1");
        var e2 = Product("Platinum Exhibitor ELDK27 - Booth E-2", "Platinum Exhibitor", ShopGroup.Booths, 13500, "E-2");
        e1.WooProductId = 117; e2.WooProductId = 116;
        var orders = new[]
        {
            new CommunityHub.Core.Integrations.WooOrder(1, "completed", "a@x.test", "Robopack ApS", "cm-7", null,
                new[] { new CommunityHub.Core.Integrations.WooLineItem(117, "Platinum E-1", "") }),
            new CommunityHub.Core.Integrations.WooOrder(2, "processing", "b@x.test", "glueckkanja AG", null, null,
                new[] { new CommunityHub.Core.Integrations.WooLineItem(116, "Platinum E-2", "") }),
        };
        var names = new Dictionary<string, string> { ["cm-7"] = "Robopack" };            // the public name wins
        var soldTo = ShopPackageAvailability.SoldTo(new[] { e1, e2 }, orders, names);

        var now = DateTimeOffset.UtcNow;
        var live = new Dictionary<int, ShopAvailability> { [e1.Id] = new(false, 0, false, true, now), [e2.Id] = new(false, 0, false, true, now) };
        var row = ShopPackageAvailability.BuildRows(new[] { e1, e2 }, live, Array.Empty<ShopPackageAvailability.Hold>(), names, soldTo).Single();

        Assert.Equal(new[] { "Robopack", "glueckkanja AG" }, row.Takers.Select(t => t.Company));
        Assert.All(row.Takers, t => Assert.True(t.Confirmed));
    }

    [Fact]
    public void A_package_in_the_summary_links_to_its_most_specific_page()
    {
        // Operator 2026-09-27: "can the overview include links to the other pages, so they can see what is included".
        var s1 = Product("Sponsor Long 60 min Session - ELDK27 - timeslot: 1 (est. time: 10:00-11:00)", "Session (60 min)", ShopGroup.Sessions, 8000, slot: "1");
        var row = ShopPackageAvailability.BuildRows(new[] { s1 }, new Dictionary<int, ShopAvailability>(), Array.Empty<ShopPackageAvailability.Hold>(), null).Single();
        var pages = new[]
        {
            new ShopCatalogPage { Slug = "deck-sponsor-sessions", SortOrder = 10, ProductMatch = "Sponsor Sessions" },
            new ShopCatalogPage { Slug = "deck-sponsor-long-60-min-session", SortOrder = 20, ProductMatch = "Sponsor Long 60 min Session" },
            new ShopCatalogPage { Slug = "deck-summary", SortOrder = 5, AvailabilityView = ShopCatalogAvailabilityView.Summary },
            new ShopCatalogPage { Slug = "deck-coffee", SortOrder = 1, ProductMatch = "Coffee Sponsor" },
        };

        Assert.Equal("deck-sponsor-long-60-min-session", ShopPackageAvailability.PageFor(row, pages)?.Slug);
        Assert.Null(ShopPackageAvailability.PageFor(row, pages.Take(0)));
    }
}
