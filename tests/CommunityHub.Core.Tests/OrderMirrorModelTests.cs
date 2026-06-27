using System.Linq;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Model + relationship tests for the order-level Zoho Backstage mirror
/// (REQUIREMENTS §125): the new <see cref="Order"/> entity, its
/// (EventId, BackstageOrderId) natural key, the SOFT-CANCEL <see cref="MirrorState"/>
/// columns on both <see cref="Order"/> and <see cref="Attendee"/>, and the
/// Attendee→Order link by Backstage order id. EF in-memory + model-metadata
/// assertions only — sync/telemetry are deliberately unchanged.
/// </summary>
public sealed class OrderMirrorModelTests
{
    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"ordermirror-{System.Guid.NewGuid():N}").Options);

    private static async Task SeedEventAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Order_persists_with_active_default_and_round_trips_fields()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        db.Orders.Add(new Order
        {
            EventId = EventId,
            BackstageOrderId = "ord-1",
            BuyerName = "Bea Buyer",
            BuyerEmail = "bea@x.dk",
            CompanyName = "ACME",
            Country = "Denmark",
            CountryCode = "DK",
            City = "Copenhagen",
            Postcode = "1000",
            TaxId = "DK12345678",
            OrderStatus = "completed",
            RawJson = "{\"id\":\"ord-1\"}",
        });
        await db.SaveChangesAsync();

        var saved = await db.Orders.SingleAsync();
        // MirrorState defaults to Active; bookkeeping timestamps are stamped.
        Assert.Equal(MirrorState.Active, saved.MirrorState);
        Assert.Null(saved.CancelledAt);
        Assert.NotEqual(default, saved.LastSyncedAt);
        Assert.NotEqual(default, saved.CreatedAt);
        Assert.Equal("ACME", saved.CompanyName);
        Assert.Equal("DK", saved.CountryCode);
        Assert.Equal("{\"id\":\"ord-1\"}", saved.RawJson);
    }

    [Fact]
    public async Task Attendee_links_to_order_by_backstage_order_id()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        db.Orders.Add(new Order { EventId = EventId, BackstageOrderId = "ord-42", CompanyName = "ACME" });
        await db.SaveChangesAsync();

        // Two tickets on the same order — linked by (EventId, OrderId) → (EventId, BackstageOrderId).
        db.Attendees.AddRange(
            new Attendee { EventId = EventId, Email = "a@x.dk", OrderId = "ord-42", BackstageTicketId = "t1" },
            new Attendee { EventId = EventId, Email = "b@x.dk", OrderId = "ord-42", BackstageTicketId = "t2" });
        await db.SaveChangesAsync();

        // One order has many attendees; the inverse navigation resolves the order.
        var order = await db.Orders.Include(o => o.Attendees).SingleAsync();
        Assert.Equal(2, order.Attendees.Count);

        var attendee = await db.Attendees.Include(a => a.Order).FirstAsync(a => a.Email == "a@x.dk");
        Assert.NotNull(attendee.Order);
        Assert.Equal("ord-42", attendee.Order!.BackstageOrderId);
        Assert.Equal("ACME", attendee.Order.CompanyName);
    }

    [Fact]
    public async Task Attendee_with_no_order_id_has_no_linked_order()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        // Legacy row: no OrderId ⇒ the optional FK is unset, not dangling.
        db.Attendees.Add(new Attendee { EventId = EventId, Email = "legacy@x.dk", OrderId = null });
        await db.SaveChangesAsync();

        var attendee = await db.Attendees.Include(a => a.Order).SingleAsync();
        Assert.Null(attendee.Order);
    }

    [Fact]
    public async Task Soft_cancel_marks_mirror_state_without_dropping_the_rows()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        var cancelledAt = new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);
        db.Orders.Add(new Order { EventId = EventId, BackstageOrderId = "ord-9" });
        db.Attendees.Add(new Attendee
        {
            EventId = EventId, Email = "c@x.dk", OrderId = "ord-9",
            BackstageTicketId = "t9", TicketStatus = TicketStatus.TwoDay,
        });
        await db.SaveChangesAsync();

        // SOFT-CANCEL (§128): flip MirrorState + stamp CancelledAt; the row is KEPT.
        var order = await db.Orders.SingleAsync();
        order.MirrorState = MirrorState.Cancelled;
        order.CancelledAt = cancelledAt;
        var ticket = await db.Attendees.SingleAsync();
        ticket.MirrorState = MirrorState.Cancelled;
        ticket.CancelledAt = cancelledAt;
        await db.SaveChangesAsync();

        // Rows survive; cancellation rides MirrorState, NOT TicketStatus (still TwoDay).
        var reloaded = await db.Attendees.SingleAsync();
        Assert.Equal(MirrorState.Cancelled, reloaded.MirrorState);
        Assert.Equal(cancelledAt, reloaded.CancelledAt);
        Assert.Equal(TicketStatus.TwoDay, reloaded.TicketStatus);

        // An "active only" query (the §128 count semantics) excludes the cancelled rows.
        Assert.Empty(await db.Orders.Where(o => o.MirrorState == MirrorState.Active).ToListAsync());
        Assert.Empty(await db.Attendees.Where(a => a.MirrorState == MirrorState.Active).ToListAsync());
    }

    [Fact]
    public void Model_has_order_natural_key_and_attendee_fk_by_backstage_order_id()
    {
        using var db = NewDb();

        var order = db.Model.FindEntityType(typeof(Order))!;
        // Natural key (EventId, BackstageOrderId) — the Attendee→Order principal key.
        Assert.Contains(order.GetKeys(), k =>
            !k.IsPrimaryKey() &&
            k.Properties.Select(p => p.Name).SequenceEqual(new[] { "EventId", "BackstageOrderId" }));
        Assert.Contains(order.GetIndexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { "EventId", "MirrorState" }));

        var attendee = db.Model.FindEntityType(typeof(Attendee))!;
        var fk = attendee.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(Order));
        Assert.True(fk.Properties.Select(p => p.Name).SequenceEqual(new[] { "EventId", "OrderId" }));
        Assert.True(fk.PrincipalKey.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { "EventId", "BackstageOrderId" }));
        // Optional link (legacy null-OrderId rows) + no second cascade path from Event.
        Assert.False(fk.IsRequired);
        Assert.Equal(DeleteBehavior.NoAction, fk.DeleteBehavior);
    }
}
