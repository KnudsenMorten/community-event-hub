using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §743 C3 — CEH is the master of sessions, and the sync must never overwrite silently. Plus the
/// backfill, which is the reason storing RAW responses (rather than computed scores) is worth the
/// discipline: presses collected before their session existed can still be attributed afterwards.
/// </summary>
public sealed class EvaluationSessionSyncTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2027, 2, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day = new(2027, 2, 9, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static async Task<CommunityHubDbContext> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "T", Code = "T27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static Session CehSession(
        string title, string? room, int startHour, int endHour, string? track = null) =>
        new()
        {
            EventId = EventId, Title = title, Room = room, Track = track,
            StartsAt = Day.AddHours(startHour), EndsAt = Day.AddHours(endHour),
        };

    private static EvaluationSessionSyncService NewSync(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now));

    [Fact]
    public async Task Sessions_and_their_rooms_are_created_with_a_30_minute_collection_window()
    {
        using var db = await SeedAsync();
        db.Sessions.Add(CehSession("Keynote", "Room 1", 9, 10, "Cloud"));
        await db.SaveChangesAsync();

        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(1, result.RoomsCreated);
        Assert.Equal(1, result.SessionsCreated);

        var s = db.EvaluationSessions.Single();
        Assert.Equal("Keynote", s.Title);
        Assert.Equal("Cloud", s.TrackName);
        Assert.Equal(Day.AddHours(9), s.CollectionWindowOpensAt);
        Assert.Equal(Day.AddHours(10).AddMinutes(30), s.CollectionWindowClosesAt);   // +grace
        Assert.Equal(60, s.ScheduledLengthMinutes);
        Assert.Equal("Room 1", db.EvaluationRooms.Single().Name);
    }

    [Fact]
    public async Task Room_names_that_differ_only_in_case_or_spacing_are_ONE_room()
    {
        // 🔒 Otherwise "Room 1", "room  1" and "Room 1 " become three rooms with one device
        // between them, and two thirds of the presses stop being attributed.
        using var db = await SeedAsync();
        db.Sessions.Add(CehSession("A", "Room 1", 9, 10));
        db.Sessions.Add(CehSession("B", "room  1 ", 11, 12));
        await db.SaveChangesAsync();

        await NewSync(db).SyncAsync(EventId);

        Assert.Single(db.EvaluationRooms);
    }

    [Fact]
    public async Task A_room_change_in_CEH_wins_and_the_replaced_value_is_NAMED()
    {
        // 🔥 The change that would otherwise vanish and take attribution with it. A wrong figure
        // has to be traceable back to the moment the session moved.
        using var db = await SeedAsync();
        var ceh = CehSession("Keynote", "Room 1", 9, 10);
        db.Sessions.Add(ceh);
        await db.SaveChangesAsync();
        await NewSync(db).SyncAsync(EventId);

        ceh.Room = "Room 2";
        await db.SaveChangesAsync();
        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(1, result.SessionsUpdated);
        Assert.Contains(result.Changes, c => c.Contains("room 'Room 1' → 'Room 2'"));
        Assert.Equal("Room 2", db.EvaluationSessions.Single().Room?.Name
                               ?? db.EvaluationRooms.Single(r => r.Id == db.EvaluationSessions.Single().RoomId).Name);
    }

    [Fact]
    public async Task A_time_change_moves_the_collection_window_with_it()
    {
        using var db = await SeedAsync();
        var ceh = CehSession("Keynote", "Room 1", 9, 10);
        db.Sessions.Add(ceh);
        await db.SaveChangesAsync();
        await NewSync(db).SyncAsync(EventId);

        ceh.StartsAt = Day.AddHours(14);
        ceh.EndsAt = Day.AddHours(15);
        await db.SaveChangesAsync();
        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Contains(result.Changes, c => c.Contains("time"));
        var s = db.EvaluationSessions.Single();
        Assert.Equal(Day.AddHours(14), s.CollectionWindowOpensAt);
        Assert.Equal(Day.AddHours(15).AddMinutes(30), s.CollectionWindowClosesAt);
    }

    [Fact]
    public async Task Service_sessions_and_undated_sessions_are_not_mirrored()
    {
        using var db = await SeedAsync();
        var service = CehSession("Lunch", "Foyer", 12, 13);
        service.IsServiceSession = true;
        db.Sessions.Add(service);
        db.Sessions.Add(new Session { EventId = EventId, Title = "TBD", Room = "Room 1" });  // no times
        await db.SaveChangesAsync();

        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(0, result.SessionsCreated);
        Assert.Empty(db.EvaluationSessions);
    }

    [Fact]
    public async Task A_session_with_no_room_is_reported_because_it_can_never_collect()
    {
        using var db = await SeedAsync();
        db.Sessions.Add(CehSession("Roomless", null, 9, 10));
        await db.SaveChangesAsync();

        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Contains(result.Warnings, w => w.Contains("no room"));
        Assert.Null(db.EvaluationSessions.Single().RoomId);
    }

    [Fact]
    public async Task Overlapping_sessions_in_ONE_room_are_reported_as_unattributable()
    {
        // Attribution would be genuinely ambiguous and no tie-break can recover the attendee's
        // intent, so the organiser must be told to fix the schedule in CEH.
        using var db = await SeedAsync();
        db.Sessions.Add(CehSession("A", "Room 1", 9, 11));
        db.Sessions.Add(CehSession("B", "Room 1", 10, 12));
        await db.SaveChangesAsync();

        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Contains(result.Warnings, w => w.Contains("OVERLAP"));
    }

    [Fact]
    public async Task Back_to_back_sessions_are_NOT_reported_as_an_overlap()
    {
        using var db = await SeedAsync();
        db.Sessions.Add(CehSession("A", "Room 1", 9, 10));
        db.Sessions.Add(CehSession("B", "Room 1", 10, 11));
        await db.SaveChangesAsync();

        var result = await NewSync(db).SyncAsync(EventId);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("OVERLAP"));
    }

    // ---- backfill --------------------------------------------------------------------------

    [Fact]
    public async Task Presses_collected_BEFORE_their_session_existed_are_attributed_retrospectively()
    {
        // 🔥 This is what storing raw responses buys. C4a shipped before C3, so real presses were
        // stored with SessionId null. They are not lost — the sync attributes them.
        using var db = await SeedAsync();
        db.Sessions.Add(CehSession("Keynote", "Room 1", 9, 10));
        await db.SaveChangesAsync();

        // A device in Room 1, and a press from during the keynote, stored unattributed.
        await NewSync(db).SyncAsync(EventId);
        var room = db.EvaluationRooms.Single();
        db.EvaluationDevices.Add(new EvaluationDevice
        {
            EventId = EventId, SerialNumber = "dev-1", KeyHash = "x", RoomId = room.Id,
            CreatedAt = Now, UpdatedAt = Now,
        });
        db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SerialNumber = "dev-1", Rating = 4,
            CollectionTimestamp = Day.AddHours(9).AddMinutes(30),
            ReceivedTimestamp = Now, DeviceRecordId = "r-1", SessionId = null,
        });
        await db.SaveChangesAsync();

        var backfilled = await NewSync(db).BackfillAsync(EventId);

        Assert.Equal(1, backfilled);
        Assert.Equal(db.EvaluationSessions.Single().Id, db.EvaluationResponses.Single().SessionId);
    }

    [Fact]
    public async Task Backfill_never_RE_points_an_already_attributed_response()
    {
        // 🔒 Re-pointing would silently move a press from one speaker to another. Only NULL is filled.
        using var db = await SeedAsync();
        db.Sessions.Add(CehSession("Keynote", "Room 1", 9, 10));
        await db.SaveChangesAsync();
        await NewSync(db).SyncAsync(EventId);
        var room = db.EvaluationRooms.Single();

        db.EvaluationDevices.Add(new EvaluationDevice
        {
            EventId = EventId, SerialNumber = "dev-1", KeyHash = "x", RoomId = room.Id,
            CreatedAt = Now, UpdatedAt = Now,
        });
        db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SerialNumber = "dev-1", Rating = 4,
            CollectionTimestamp = Day.AddHours(9).AddMinutes(30),
            ReceivedTimestamp = Now, DeviceRecordId = "r-1",
            SessionId = 99999,   // already attributed elsewhere
        });
        await db.SaveChangesAsync();

        var backfilled = await NewSync(db).BackfillAsync(EventId);

        Assert.Equal(0, backfilled);
        Assert.Equal(99999, db.EvaluationResponses.Single().SessionId);
    }

    [Fact]
    public async Task A_press_outside_every_window_stays_venue_and_date_only()
    {
        using var db = await SeedAsync();
        db.Sessions.Add(CehSession("Keynote", "Room 1", 9, 10));
        await db.SaveChangesAsync();
        await NewSync(db).SyncAsync(EventId);
        var room = db.EvaluationRooms.Single();

        db.EvaluationDevices.Add(new EvaluationDevice
        {
            EventId = EventId, SerialNumber = "dev-1", KeyHash = "x", RoomId = room.Id,
            CreatedAt = Now, UpdatedAt = Now,
        });
        db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SerialNumber = "dev-1", Rating = 4,
            CollectionTimestamp = Day.AddHours(20),   // long after the tail closed
            ReceivedTimestamp = Now, DeviceRecordId = "r-1", SessionId = null,
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, await NewSync(db).BackfillAsync(EventId));
        Assert.Null(db.EvaluationResponses.Single().SessionId);   // kept, not discarded
    }

    [Fact]
    public async Task An_UNLINKED_device_attributes_nothing_until_someone_links_it()
    {
        using var db = await SeedAsync();
        db.Sessions.Add(CehSession("Keynote", "Room 1", 9, 10));
        await db.SaveChangesAsync();
        await NewSync(db).SyncAsync(EventId);

        db.EvaluationDevices.Add(new EvaluationDevice
        {
            EventId = EventId, SerialNumber = "dev-1", KeyHash = "x", RoomId = null,   // not linked
            CreatedAt = Now, UpdatedAt = Now,
        });
        db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SerialNumber = "dev-1", Rating = 4,
            CollectionTimestamp = Day.AddHours(9).AddMinutes(30),
            ReceivedTimestamp = Now, DeviceRecordId = "r-1", SessionId = null,
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, await NewSync(db).BackfillAsync(EventId));
    }
}
