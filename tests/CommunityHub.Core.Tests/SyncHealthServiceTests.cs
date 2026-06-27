using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="SyncHealthService"/> — the read-only organizer CEH⇄Zoho
/// Sync-Health dashboard (REQUIREMENTS §132 / the §125 trust goal). Uses the EF Core
/// InMemory provider so the real DbContext mapping + LINQ run, seeded so the status
/// (fresh / stale / never), the active-vs-cancelled mirror counts, and the drift
/// indicators are each deterministic. A second edition's rows prove event-scoping.
/// </summary>
public sealed class SyncHealthServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    // Fixed "now" so every age / staleness assertion is deterministic.
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"synchealth-{Guid.NewGuid():N}")
            .Options);

    private static SyncHealthService NewSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now));

    private static DateTimeOffset HoursAgo(double h) => Now.AddHours(-h);

    private static async Task SeedEventsAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "SH27", CommunityName = "Sync Health Test",
            DisplayName = "Sync Health Test 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.Events.Add(new Event
        {
            Id = OtherEventId, Code = "OTHER", CommunityName = "Other", DisplayName = "Other 2027",
            StartDate = new DateOnly(2027, 5, 1), EndDate = new DateOnly(2027, 5, 2), IsActive = false,
        });
        await db.SaveChangesAsync();
    }

    private static Order Ord(int eventId, string id, MirrorState state = MirrorState.Active) => new()
    {
        EventId = eventId, BackstageOrderId = id, MirrorState = state,
        CancelledAt = state == MirrorState.Cancelled ? HoursAgo(2) : null,
    };

    private static Attendee Att(int eventId, string ticketId, string? orderId, MirrorState state = MirrorState.Active) => new()
    {
        EventId = eventId, BackstageTicketId = ticketId, OrderId = orderId,
        Email = ticketId + "@x.test", FirstName = "F", LastName = "L",
        MirrorState = state, CancelledAt = state == MirrorState.Cancelled ? HoursAgo(2) : null,
    };

    private static async Task SeedMarkerAsync(
        CommunityHubDbContext db, int eventId, DateTimeOffset? lastSuccess, DateTimeOffset? lastWebhook = null,
        string? summary = null)
    {
        db.SyncRuns.Add(new SyncRun
        {
            EventId = eventId, Key = SyncRun.AttendeeBackstageKey,
            LastSuccessAt = lastSuccess ?? default, LastWebhookAt = lastWebhook, Summary = summary,
        });
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- status

    [Fact]
    public async Task Recent_sync_is_in_sync()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(1));

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(SyncHealthStatus.InSync, snap.Status);
        Assert.True(snap.HasEverSynced);
        Assert.Equal(TimeSpan.FromHours(1), snap.SyncAge);
    }

    [Fact]
    public async Task Sync_older_than_window_is_stale()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        // Window is 6h; 9h ago is past it.
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(9));

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(SyncHealthStatus.Stale, snap.Status);
        Assert.True(snap.SyncAge > SyncHealthService.SyncStaleAfter);
    }

    [Fact]
    public async Task Sync_exactly_at_window_boundary_is_still_in_sync()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        // Exactly at the window edge is NOT yet stale (strictly greater-than is stale).
        await SeedMarkerAsync(db, EventId, lastSuccess: Now - SyncHealthService.SyncStaleAfter);

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(SyncHealthStatus.InSync, snap.Status);
    }

    [Fact]
    public async Task No_marker_means_never_synced()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(SyncHealthStatus.NeverSynced, snap.Status);
        Assert.False(snap.HasEverSynced);
        Assert.Null(snap.LastSuccessAt);
        Assert.Null(snap.SyncAge);
    }

    [Fact]
    public async Task Future_sync_stamp_clamps_age_to_zero()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: Now.AddHours(1)); // clock skew

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(TimeSpan.Zero, snap.SyncAge);
        Assert.Equal(SyncHealthStatus.InSync, snap.Status);
    }

    // ---------------------------------------------------------------- webhook

    [Fact]
    public async Task Last_webhook_stamp_is_surfaced_with_age()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(1), lastWebhook: HoursAgo(2));

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(HoursAgo(2), snap.LastWebhookAt);
        Assert.Equal(TimeSpan.FromHours(2), snap.WebhookAge);
    }

    [Fact]
    public async Task No_webhook_leaves_webhook_null()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(1));

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Null(snap.LastWebhookAt);
        Assert.Null(snap.WebhookAge);
    }

    // ---------------------------------------------------------------- counts

    [Fact]
    public async Task Counts_split_active_and_cancelled_per_record_type()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(1));

        // 2 active orders, 1 cancelled. Each active order has a matching active attendee.
        db.Orders.AddRange(Ord(EventId, "o1"), Ord(EventId, "o2"), Ord(EventId, "o3", MirrorState.Cancelled));
        db.Attendees.AddRange(
            Att(EventId, "t1", "o1"),
            Att(EventId, "t2", "o2"),
            Att(EventId, "t3", "o3", MirrorState.Cancelled)); // cancelled ticket on cancelled order
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(2, snap.OrdersActive);
        Assert.Equal(1, snap.OrdersCancelled);
        Assert.Equal(3, snap.OrdersTotal);
        Assert.Equal(2, snap.AttendeesActive);
        Assert.Equal(1, snap.AttendeesCancelled);
        Assert.Equal(3, snap.AttendeesTotal);
        Assert.False(snap.HasDrift);
    }

    [Fact]
    public async Task Last_run_summary_is_surfaced()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(1), summary: "5 active orders / 9 active attendees");

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal("5 active orders / 9 active attendees", snap.LastRunSummary);
    }

    // ---------------------------------------------------------------- drift

    [Fact]
    public async Task Attendee_with_null_order_is_drift()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(1));

        db.Orders.Add(Ord(EventId, "o1"));
        db.Attendees.AddRange(
            Att(EventId, "t1", "o1"),
            Att(EventId, "t2", orderId: null)); // orphan: no order link
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(1, snap.AttendeesWithoutOrder);
        Assert.True(snap.HasDrift);
    }

    [Fact]
    public async Task Attendee_pointing_at_missing_order_is_drift()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(1));

        db.Orders.Add(Ord(EventId, "o1"));
        // t2 references an order id the mirror doesn't hold.
        db.Attendees.AddRange(Att(EventId, "t1", "o1"), Att(EventId, "t2", "ghost-order"));
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(1, snap.AttendeesWithoutOrder);
        Assert.True(snap.HasDrift);
    }

    [Fact]
    public async Task Cancelled_attendee_without_order_is_not_counted_as_drift()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(1));

        // Only ACTIVE attendees count toward drift.
        db.Attendees.Add(Att(EventId, "t1", orderId: null, state: MirrorState.Cancelled));
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(0, snap.AttendeesWithoutOrder);
    }

    [Fact]
    public async Task Active_order_with_no_active_attendee_is_drift()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(1));

        db.Orders.AddRange(Ord(EventId, "o1"), Ord(EventId, "o2"));
        // o1 has an active attendee; o2 only has a cancelled one ⇒ empty shell.
        db.Attendees.AddRange(
            Att(EventId, "t1", "o1"),
            Att(EventId, "t2", "o2", MirrorState.Cancelled));
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(1, snap.OrdersWithoutAttendees);
        Assert.True(snap.HasDrift);
    }

    [Fact]
    public async Task No_drift_when_every_active_order_and_ticket_is_linked()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(1));

        db.Orders.Add(Ord(EventId, "o1"));
        db.Attendees.AddRange(Att(EventId, "t1", "o1"), Att(EventId, "t2", "o1"));
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(0, snap.AttendeesWithoutOrder);
        Assert.Equal(0, snap.OrdersWithoutAttendees);
        Assert.False(snap.HasDrift);
    }

    // ---------------------------------------------------------------- scoping

    [Fact]
    public async Task Everything_is_event_scoped()
    {
        await using var db = NewDb();
        await SeedEventsAsync(db);
        await SeedMarkerAsync(db, EventId, lastSuccess: HoursAgo(1));
        await SeedMarkerAsync(db, OtherEventId, lastSuccess: HoursAgo(99));

        // Other edition's orders/attendees must not bleed in.
        db.Orders.Add(Ord(OtherEventId, "ox"));
        db.Attendees.Add(Att(OtherEventId, "tx", orderId: null));
        db.Orders.Add(Ord(EventId, "o1"));
        db.Attendees.Add(Att(EventId, "t1", "o1"));
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(1, snap.OrdersActive);
        Assert.Equal(1, snap.AttendeesActive);
        Assert.Equal(0, snap.AttendeesWithoutOrder); // the other edition's orphan is excluded
        Assert.Equal(HoursAgo(1), snap.LastSuccessAt);
    }
}
