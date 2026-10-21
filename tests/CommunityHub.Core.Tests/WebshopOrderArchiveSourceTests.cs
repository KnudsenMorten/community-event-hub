using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1312 — the old webshop's orders are read from CEH's own store (operator 2026-10-01: "store the information in ceh about the
/// orders/products that was bought using the old webshop, so we dont need to lookup against woocommerce" · "remember that the
/// previous orders and any dependency must be workin"). They were stored once on 2026-10-01; the webshop client is gone.
/// </summary>
public sealed class WebshopOrderArchiveSourceTests
{
    private static async Task<(CommunityHubDbContext Db, int EventId)> NewDbAsync(bool active = true)
    {
        var db = ScenarioFixture.NewDb();
        var ev = new Event { CommunityName = "Demo", Code = $"T{Guid.NewGuid():N}"[..8], DisplayName = "Demo", IsActive = active };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return (db, ev.Id);
    }

    private static void Store(CommunityHubDbContext db, int eventId, WooOrder o) =>
        db.WebshopOrderArchives.Add(new WebshopOrderArchive
        {
            OrderId = o.OrderId, EventId = eventId, Status = o.Status, CompanyId = o.CompanyId, BillingCompany = o.BillingCompany,
            BillingEmail = o.BillingEmail, CreatedAt = o.CreatedAt, Json = WebshopOrderArchiveSource.Write(o), ArchivedAt = DateTimeOffset.UnixEpoch,
        });

    /// <summary>Every field a reader uses survives the store — the invoice key, the company, categories, paid prices and coupons.</summary>
    [Fact]
    public async Task A_stored_order_reads_back_with_every_field_the_readers_use()
    {
        var (db, eventId) = await NewDbAsync();
        var created = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        Store(db, eventId, new WooOrder(10871, "completed", "buyer@example.test", "Example ApS", "20", created,
            new[]
            {
                new WooLineItem(501, "Gold Booth E-21", "Booths, Gold", 1, 900m, 1000m, 900m),
                new WooLineItem(502, "TV 55\"", "TV Rental", 2, 150m, 300m, 300m),
            },
            new[] { "eldk-gold-booth-included" }));
        await db.SaveChangesAsync();

        var o = Assert.Single(await new WebshopOrderArchiveSource(db).GetOrdersAsync("completed"));

        Assert.Equal(10871, o.OrderId);                               // the e-conomic reference key WebshopOrderId-10871
        Assert.Equal("20", o.CompanyId);
        Assert.Equal("Example ApS", o.BillingCompany);
        Assert.Equal("buyer@example.test", o.BillingEmail);
        Assert.Equal(created, o.CreatedAt);
        Assert.Equal(new[] { "eldk-gold-booth-included" }, o.CouponCodes);
        var booth = o.LineItems.Single(l => l.ProductId == 501);
        Assert.Equal("Gold Booth E-21", booth.ProductName);            // the booth label is read from the name
        Assert.Equal("Booths, Gold", booth.CategoriesText);             // captured when stored — they existed only in the webshop
        Assert.Equal((900m, 1000m, 900m), (booth.UnitPrice, booth.LineSubtotal, booth.LineTotal));   // the coupon discount survives
        Assert.Equal(2, o.LineItems.Single(l => l.ProductId == 502).Quantity);
    }

    /// <summary>Statuses filter exactly as the webshop's did (comma-separated, case-insensitive); only the ACTIVE edition's orders.</summary>
    [Fact]
    public async Task Orders_are_filtered_by_status_and_only_the_active_editions_are_read()
    {
        var (db, eventId) = await NewDbAsync();
        var other = new Event { CommunityName = "Old", Code = $"O{Guid.NewGuid():N}"[..8], DisplayName = "Old", IsActive = false };
        db.Events.Add(other);
        await db.SaveChangesAsync();
        WooOrder Order(long id, string status) => new(id, status, "a@example.test", "A", "30", DateTimeOffset.UnixEpoch.AddDays(id), Array.Empty<WooLineItem>());
        Store(db, eventId, Order(1, "completed"));
        Store(db, eventId, Order(2, "processing"));
        Store(db, eventId, Order(3, "cancelled"));
        Store(db, other.Id, Order(4, "completed"));
        await db.SaveChangesAsync();
        var source = new WebshopOrderArchiveSource(db);

        Assert.Equal(new long[] { 1 }, (await source.GetOrdersAsync("completed")).Select(o => o.OrderId));
        Assert.Equal(new long[] { 2, 1 }, (await source.GetOrdersAsync("Completed, processing")).Select(o => o.OrderId));   // newest first
        Assert.Equal(new long[] { 3 }, (await source.GetOrdersAsync("cancelled,refunded")).Select(o => o.OrderId));
    }

    [Fact]
    public async Task No_active_edition_or_no_stored_orders_reads_as_none()
    {
        var (db, _) = await NewDbAsync(active: false);
        Assert.Empty(await new WebshopOrderArchiveSource(db).GetOrdersAsync("completed"));

        var (db2, _) = await NewDbAsync();
        Assert.Empty(await new WebshopOrderArchiveSource(db2).GetOrdersAsync("completed"));
    }
}
