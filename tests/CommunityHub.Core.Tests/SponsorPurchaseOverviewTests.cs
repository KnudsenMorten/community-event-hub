using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Sponsors;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1359 — tier, categories and products per sponsor company for the organizer Sponsors page. Operator 2026-10-19:
/// *"their tier, their extra products, their sponsor category, etc where can i find that"* · *"my event partner sabine
/// needs it"*. Only real purchases: completed webshop orders (signed CEH shop orders go through the same reader).
/// </summary>
public sealed class SponsorPurchaseOverviewTests
{
    private static async Task<(CommunityHubDbContext Db, int EventId)> NewDbAsync()
    {
        var db = ScenarioFixture.NewDb();
        var ev = new Event { CommunityName = "Demo", Code = $"T{Guid.NewGuid():N}"[..8], DisplayName = "Demo", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return (db, ev.Id);
    }

    private static void Store(CommunityHubDbContext db, int eventId, long orderId, string status, string companyId,
                              params (string Name, string Categories, int Qty)[] lines)
    {
        var order = new WooOrder(orderId, status, "buyer@example.test", "Example ApS", companyId, DateTimeOffset.UnixEpoch,
            lines.Select((l, i) => new WooLineItem(i + 1, l.Name, l.Categories, l.Qty)).ToList());
        db.WebshopOrderArchives.Add(new WebshopOrderArchive
        {
            OrderId = orderId, EventId = eventId, Status = status, CompanyId = companyId, BillingCompany = "Example ApS",
            BillingEmail = "buyer@example.test", CreatedAt = DateTimeOffset.UnixEpoch,
            Json = WebshopOrderArchiveSource.Write(order), ArchivedAt = DateTimeOffset.UnixEpoch,
        });
    }

    [Fact]
    public async Task A_sponsor_shows_its_current_tier_categories_and_every_product_bought()
    {
        var (db, eventId) = await NewDbAsync();
        db.SponsorInfos.Add(new SponsorInfo { EventId = eventId, SponsorCompanyId = "19", CompanyName = "Patch My PC",
                                              Tier = BoothTier.Gold, HasCurrentBoothOrder = true });
        Store(db, eventId, 10760, "completed", "19",
            ("Sponsor Long 60 min Session - ELDK27 - timeslot: 3 (est. time: 13:30-14:30)", "Sponsor Sessions", 1),
            ("Booth E-12 Gold", "Gold Tier Package, Exhibitor", 1),
            ("Booth: Chair, Tolix Stool Outdoor, black", "Booth Addons", 2));
        await db.SaveChangesAsync();

        var s = (await SponsorPurchaseOverview.BuildAsync(db, eventId))["19"];

        Assert.Equal("Gold", s.Tier);
        Assert.Equal(new[] { "Booth Addons", "Exhibitor", "Gold Tier Package", "Sponsor Sessions" }, s.Categories);
        Assert.Contains("2 × Booth: Chair, Tolix Stool Outdoor, black", s.Products);
        Assert.Contains("Sponsor Long 60 min Session - ELDK27 - timeslot: 3 (est. time: 13:30-14:30)", s.Products);
    }

    [Fact]
    public async Task A_cancelled_order_shows_nothing_and_a_lapsed_tier_is_not_shown()
    {
        // 2linkIT on PROD: a cancelled TEST session order, and a Gold tier left behind by a cancelled booth (§1348).
        var (db, eventId) = await NewDbAsync();
        db.SponsorInfos.Add(new SponsorInfo { EventId = eventId, SponsorCompanyId = "12", CompanyName = "2linkIT",
                                              Tier = BoothTier.Gold, HasCurrentBoothOrder = false });
        Store(db, eventId, 10811, "cancelled", "12",
            ("Sponsor Expo Intro 20 min Session - ELDK27 - timeslot: 0 (TEST)", "Sponsor Sessions", 1));
        await db.SaveChangesAsync();

        var s = (await SponsorPurchaseOverview.BuildAsync(db, eventId))["12"];

        Assert.Null(s.Tier);
        Assert.Empty(s.Categories);
        Assert.Empty(s.Products);
    }

    [Fact]
    public async Task The_same_product_on_two_orders_is_counted_once_with_its_total_quantity()
    {
        var (db, eventId) = await NewDbAsync();
        Store(db, eventId, 1, "completed", "23", ("Extra staff ticket", "Booth Addons", 1));
        Store(db, eventId, 2, "completed", "23", ("Extra staff ticket", "Booth Addons", 2));
        await db.SaveChangesAsync();

        var s = (await SponsorPurchaseOverview.BuildAsync(db, eventId))["23"];

        Assert.Equal(new[] { "3 × Extra staff ticket" }, s.Products);
        Assert.Null(s.Tier);   // no sponsor row yet: products still show
    }
}
