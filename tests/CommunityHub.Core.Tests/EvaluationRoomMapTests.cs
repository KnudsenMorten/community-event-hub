using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1350 — the room mapper: device → PHYSICAL room → schedule rooms → session, the press time deciding.
/// Operator 2026-10-19: *"room 5-6-7 on preday will be split into 5 and 6-7 on main day"* · *"it is important
/// that your enine suppot this so it pulls up the next session based on the physical room device will be placed
/// in, the time, the date"*. The fixture is that venue in miniature, with the real room names.
/// </summary>
public class EvaluationRoomMapTests
{
    private const int EventId = 1;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Pre-day 9 Feb, main day 10 Feb 2027 — as on PROD.
    private static readonly DateTimeOffset PreDay = new(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MainDay = new(2027, 2, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed record Venue(
        CommunityHub.Core.Data.CommunityHubDbContext Db,
        EvaluationPhysicalRoom Room5, EvaluationPhysicalRoom Room67,
        EvaluationRoom Mc57, EvaluationRoom Main5, EvaluationRoom Main67);

    private static async Task<Venue> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        var mc57 = Room("Room-5-7-Floor 1-Max 122-MC");
        var main5 = Room("Room-5-Floor 1-Max 89");
        var main67 = Room("Room-6-7-Floor 1-Max 99");
        db.EvaluationRooms.AddRange(mc57, main5, main67);
        await db.SaveChangesAsync();

        db.EvaluationSessions.AddRange(
            Slot(mc57.Id, "Security Master Class", PreDay),
            Slot(main5.Id, "Room 5 morning talk", MainDay),
            Slot(main67.Id, "Room 6-7 morning talk", MainDay),
            Slot(main67.Id, "Room 6-7 afternoon talk", MainDay.AddHours(4)));

        // 🔑 The split: the combined pre-day room sits under BOTH physical rooms.
        var room5 = new EvaluationPhysicalRoom { EventId = EventId, Name = "Room 5" };
        var room67 = new EvaluationPhysicalRoom { EventId = EventId, Name = "Room 6-7" };
        db.EvaluationPhysicalRooms.AddRange(room5, room67);
        await db.SaveChangesAsync();
        db.EvaluationRoomPlacements.AddRange(
            new EvaluationRoomPlacement { PhysicalRoomId = room5.Id, EvaluationRoomId = mc57.Id },
            new EvaluationRoomPlacement { PhysicalRoomId = room5.Id, EvaluationRoomId = main5.Id },
            new EvaluationRoomPlacement { PhysicalRoomId = room67.Id, EvaluationRoomId = mc57.Id },
            new EvaluationRoomPlacement { PhysicalRoomId = room67.Id, EvaluationRoomId = main67.Id });
        await db.SaveChangesAsync();
        return new Venue(db, room5, room67, mc57, main5, main67);
    }

    private static EvaluationRoom Room(string name) =>
        new() { EventId = EventId, Name = name, NameKey = name.ToLowerInvariant() };

    private static EvaluationSession Slot(int roomId, string title, DateTimeOffset start)
    {
        var (opens, closes) = EvaluationAttribution.WindowFor(start, start.AddHours(1));
        return new EvaluationSession
        {
            EventId = EventId, RoomId = roomId, Title = title,
            ScheduledStart = start, ScheduledEnd = start.AddHours(1),
            CollectionWindowOpensAt = opens, CollectionWindowClosesAt = closes,
        };
    }

    private static async Task<EvaluationDevice> TabletAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, string serial, int? physicalRoomId)
    {
        var device = new EvaluationDevice
        {
            EventId = EventId, SerialNumber = serial, PhysicalRoomId = physicalRoomId, IsActive = true, IsKiosk = true,
            KeyHash = EvaluationIngestService.HashKey("k"), CreatedAt = PreDay, UpdatedAt = PreDay,
        };
        db.EvaluationDevices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    private static EvaluationKioskService Kiosk(CommunityHub.Core.Data.CommunityHubDbContext db, DateTimeOffset now) =>
        new(db, new FixedClock(now));

    // ---- the engine: physical room + date + time ----------------------------------------------

    [Fact]
    public async Task A_tablet_in_room_6_7_shows_the_combined_master_class_on_the_pre_day_and_its_own_talk_on_the_main_day()
    {
        var v = await SeedAsync();
        var tablet = await TabletAsync(v.Db, "ELDK-67", v.Room67.Id);

        Assert.Equal("Security Master Class", (await Kiosk(v.Db, PreDay.AddMinutes(10)).CurrentSessionAsync(tablet))!.Title);
        Assert.Equal("Room 6-7 morning talk", (await Kiosk(v.Db, MainDay.AddMinutes(10)).CurrentSessionAsync(tablet))!.Title);
        // Never the talk running at the same time in the OTHER half of the split room.
        Assert.NotEqual("Room 5 morning talk", (await Kiosk(v.Db, MainDay.AddMinutes(10)).CurrentSessionAsync(tablet))!.Title);
    }

    [Fact]
    public async Task Both_halves_of_a_split_room_count_down_to_the_same_master_class_the_evening_before()
    {
        var v = await SeedAsync();
        var t5 = await TabletAsync(v.Db, "ELDK-5", v.Room5.Id);
        var t67 = await TabletAsync(v.Db, "ELDK-67", v.Room67.Id);
        var evening = PreDay.AddHours(-12);

        Assert.Equal("Security Master Class", (await Kiosk(v.Db, evening).NextSessionAsync(t5))!.Title);
        Assert.Equal("Security Master Class", (await Kiosk(v.Db, evening).NextSessionAsync(t67))!.Title);
        // After the master class, each half counts down to ITS OWN main-day talk.
        var afterMc = PreDay.AddHours(3);
        Assert.Equal("Room 5 morning talk", (await Kiosk(v.Db, afterMc).NextSessionAsync(t5))!.Title);
        Assert.Equal("Room 6-7 morning talk", (await Kiosk(v.Db, afterMc).NextSessionAsync(t67))!.Title);
    }

    [Fact]
    public async Task A_press_lands_on_the_session_of_its_physical_room_at_its_date_and_time()
    {
        var v = await SeedAsync();
        var tablet = await TabletAsync(v.Db, "ELDK-67", v.Room67.Id);
        var ingest = new EvaluationIngestService(v.Db, new FixedClock(MainDay.AddHours(6)));

        await ingest.IngestAsync(tablet,
            [
                new EvaluationIngestService.IncomingRecord("pre", 4, PreDay.AddMinutes(20)),
                new EvaluationIngestService.IncomingRecord("am", 3, MainDay.AddMinutes(20)),
                new EvaluationIngestService.IncomingRecord("pm", 2, MainDay.AddHours(4).AddMinutes(20)),
            ],
            firmwareVersion: "web-kiosk/1", clockSyncedAt: MainDay, eventClosed: false);

        async Task<string> TitleOf(string recordId)
        {
            var sessionId = (await v.Db.EvaluationResponses.SingleAsync(r => r.DeviceRecordId == recordId)).SessionId;
            return (await v.Db.EvaluationSessions.SingleAsync(s => s.Id == sessionId)).Title;
        }
        Assert.Equal("Security Master Class", await TitleOf("pre"));
        Assert.Equal("Room 6-7 morning talk", await TitleOf("am"));
        Assert.Equal("Room 6-7 afternoon talk", await TitleOf("pm"));
    }

    [Fact]
    public async Task The_screen_names_the_physical_room()
    {
        var v = await SeedAsync();
        var tablet = await TabletAsync(v.Db, "ELDK-67", v.Room67.Id);

        Assert.Equal("Room 6-7", await Kiosk(v.Db, PreDay).RoomNameAsync(tablet));
    }

    [Fact]
    public async Task A_physical_room_overrides_the_old_direct_links_and_without_one_the_direct_links_still_count()
    {
        var v = await SeedAsync();
        var tablet = await TabletAsync(v.Db, "ELDK-67", physicalRoomId: null);
        tablet.RoomId = v.Main5.Id;   // a §1349 link made before the mapper existed
        await v.Db.SaveChangesAsync();

        Assert.Equal(new[] { v.Main5.Id }, await EvaluationRoomMap.RoomIdsAsync(v.Db, tablet));

        tablet.PhysicalRoomId = v.Room67.Id;
        await v.Db.SaveChangesAsync();
        Assert.Equal(new[] { v.Mc57.Id, v.Main67.Id }.OrderBy(x => x),
                     (await EvaluationRoomMap.RoomIdsAsync(v.Db, tablet)).OrderBy(x => x));
    }

    [Fact]
    public async Task Backfill_attributes_through_the_physical_room()
    {
        var v = await SeedAsync();
        var tablet = await TabletAsync(v.Db, "ELDK-5", v.Room5.Id);
        v.Db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = EventId, SerialNumber = tablet.SerialNumber, Rating = 4,
            CollectionTimestamp = PreDay.AddMinutes(20), ReceivedTimestamp = PreDay.AddMinutes(21),
            DeviceRecordId = "early", SessionId = null,
        });
        await v.Db.SaveChangesAsync();

        Assert.Equal(1, await new EvaluationSessionSyncService(v.Db, new FixedClock(MainDay)).BackfillAsync(EventId));
        var mc = await v.Db.EvaluationSessions.SingleAsync(s => s.RoomId == v.Mc57.Id);
        Assert.Equal(mc.Id, (await v.Db.EvaluationResponses.SingleAsync()).SessionId);
    }

    // ---- what an organizer must see ----------------------------------------------------------

    [Fact]
    public async Task A_schedule_room_with_sessions_in_no_physical_room_is_reported()
    {
        var v = await SeedAsync();
        var orphan = Room("Room-TreeHouse North-Floor 1-Max 96");
        v.Db.EvaluationRooms.Add(orphan);
        await v.Db.SaveChangesAsync();
        v.Db.EvaluationSessions.Add(Slot(orphan.Id, "Treehouse talk", MainDay));
        await v.Db.SaveChangesAsync();

        var warnings = await EvaluationRoomMap.WarningsAsync(v.Db, EventId);

        Assert.Contains(warnings, w => w.Text.Contains("Room-TreeHouse North-Floor 1-Max 96") && w.Text.Contains("in no physical room"));
    }

    [Fact]
    public async Task Two_schedule_rooms_with_overlapping_sessions_in_one_physical_room_are_reported()
    {
        var v = await SeedAsync();
        // A mistake: Room-5 also ticked under Room 6-7, where a talk runs at the same time.
        v.Db.EvaluationRoomPlacements.Add(new EvaluationRoomPlacement { PhysicalRoomId = v.Room67.Id, EvaluationRoomId = v.Main5.Id });
        await v.Db.SaveChangesAsync();

        var warnings = await EvaluationRoomMap.WarningsAsync(v.Db, EventId);

        Assert.Contains(warnings, w => w.Text.StartsWith("'Room 6-7'") && w.Text.Contains("overlap"));
        Assert.DoesNotContain(warnings, w => w.Text.StartsWith("'Room 5'") && w.Text.Contains("overlap"));
    }

    [Fact]
    public async Task A_physical_room_with_sessions_but_no_tablet_is_reported_and_a_correct_setup_is_silent()
    {
        var v = await SeedAsync();
        await TabletAsync(v.Db, "ELDK-5", v.Room5.Id);

        var warnings = await EvaluationRoomMap.WarningsAsync(v.Db, EventId);
        Assert.Contains(warnings, w => w.Text == "'Room 6-7' has sessions but no active tablet.");

        await TabletAsync(v.Db, "ELDK-67", v.Room67.Id);
        Assert.Empty(await EvaluationRoomMap.WarningsAsync(v.Db, EventId));
    }
}
