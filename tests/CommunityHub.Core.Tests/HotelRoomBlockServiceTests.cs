using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="HotelRoomBlockService"/> — the room-block
/// occupancy view (REQUIREMENTS §3 hotels / §20 Organizer Logistics): per-hotel
/// reserved-block vs. room-needers, under / at / over-block state, and the
/// edition roll-up incl. people who need a room but are not yet placed.
/// EF Core InMemory + a fixed clock; FAKE hotel + person names only.
/// </summary>
public sealed class HotelRoomBlockServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);

    private static (HotelRoomBlockService blocks, HotelManagementService mgmt, CommunityHubDbContext db) NewSut()
    {
        var db = TestDb.New();
        return (new HotelRoomBlockService(db),
                new HotelManagementService(db, new FixedClock(T0)),
                db);
    }

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db, string code = "TEST27")
    {
        var evt = new Event
        {
            Code = code,
            CommunityName = "Test Community",
            DisplayName = "Test Community 2027",
            StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static async Task<int> SeedPersonAsync(
        CommunityHubDbContext db, int eventId, string name, string email,
        bool needsRoom, int? hotelId = null, string? confirmation = null)
    {
        var p = new Participant
        {
            EventId = eventId, FullName = name, Email = email,
            Role = ParticipantRole.Volunteer, IsActive = true,
            HotelId = hotelId, HotelConfirmationNumber = confirmation,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.HotelBookings.Add(new HotelBooking
        {
            EventId = eventId, ParticipantId = p.Id, NeedsRoom = needsRoom,
        });
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task Empty_event_yields_empty_snapshot()
    {
        var (blocks, _, db) = NewSut();
        var eventId = await SeedEventAsync(db);

        var snap = await blocks.BuildAsync(eventId);

        Assert.Empty(snap.Hotels);
        Assert.Equal(0, snap.TotalBlock);
        Assert.Equal(0, snap.TotalAssigned);
        Assert.Equal(0, snap.UnassignedNeedingRoom);
        Assert.True(snap.PlanLooksOk); // nothing over, nobody unplaced
    }

    [Fact]
    public async Task Hotel_within_block_reports_remaining_and_ok()
    {
        var (blocks, mgmt, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var hotel = await mgmt.CreateHotelAsync(eventId, "Alpha Hotel", null, null, null, roomBlockSize: 10);

        // 3 room-needers placed; 1 placed person who does NOT need a room.
        await SeedPersonAsync(db, eventId, "Ada Fake", "ada@fake.test", needsRoom: true, hotelId: hotel.Id, confirmation: "C-1");
        await SeedPersonAsync(db, eventId, "Bo Fake", "bo@fake.test", needsRoom: true, hotelId: hotel.Id);
        await SeedPersonAsync(db, eventId, "Cy Fake", "cy@fake.test", needsRoom: true, hotelId: hotel.Id);
        await SeedPersonAsync(db, eventId, "Di Local", "di@fake.test", needsRoom: false, hotelId: hotel.Id);

        var snap = await blocks.BuildAsync(eventId);

        var line = Assert.Single(snap.Hotels);
        Assert.Equal(10, line.RoomBlockSize);
        Assert.Equal(4, line.Assigned);               // all four placed
        Assert.Equal(3, line.AssignedNeedingRoom);    // only the room-needers
        Assert.Equal(1, line.Confirmed);              // only Ada has a number
        Assert.Equal(7, line.Remaining);              // 10 - 3
        Assert.Equal(0, line.Over);
        Assert.False(line.IsOverBlock);
        Assert.Equal(30, line.Percent);               // 3/10
        Assert.True(snap.PlanLooksOk);
        Assert.Equal(10, snap.TotalBlock);
        Assert.Equal(7, snap.TotalRemaining);
    }

    [Fact]
    public async Task Oversubscribed_block_flags_over_and_breaks_the_plan()
    {
        var (blocks, mgmt, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var hotel = await mgmt.CreateHotelAsync(eventId, "Tiny Hotel", null, null, null, roomBlockSize: 2);

        for (int i = 0; i < 5; i++)
            await SeedPersonAsync(db, eventId, $"Guest{i} Fake", $"g{i}@fake.test", needsRoom: true, hotelId: hotel.Id);

        var snap = await blocks.BuildAsync(eventId);

        var line = Assert.Single(snap.Hotels);
        Assert.Equal(5, line.AssignedNeedingRoom);
        Assert.Equal(0, line.Remaining);  // floored, never negative
        Assert.Equal(3, line.Over);       // 5 - 2
        Assert.True(line.IsOverBlock);
        Assert.Equal(1, snap.HotelsOverBlock);
        Assert.False(snap.PlanLooksOk);
    }

    [Fact]
    public async Task Hotel_without_block_reports_no_block_state()
    {
        var (blocks, mgmt, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var hotel = await mgmt.CreateHotelAsync(eventId, "Unset Hotel", null, null, null); // no block size

        await SeedPersonAsync(db, eventId, "Eli Fake", "eli@fake.test", needsRoom: true, hotelId: hotel.Id);

        var snap = await blocks.BuildAsync(eventId);

        var line = Assert.Single(snap.Hotels);
        Assert.False(line.HasBlock);
        Assert.Null(line.RoomBlockSize);
        Assert.Null(line.Remaining);
        Assert.Null(line.Over);
        Assert.Null(line.Percent);
        Assert.False(line.IsOverBlock);    // no block ⇒ never "over"
        Assert.Equal(1, snap.HotelsWithoutBlock);
        Assert.Equal(0, snap.TotalBlock);  // an unset hotel contributes nothing
        Assert.True(snap.PlanLooksOk);     // a missing block doesn't by itself break the plan
    }

    [Fact]
    public async Task Unplaced_room_needers_are_counted_and_break_the_plan()
    {
        var (blocks, mgmt, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var hotel = await mgmt.CreateHotelAsync(eventId, "Alpha Hotel", null, null, null, roomBlockSize: 5);

        await SeedPersonAsync(db, eventId, "Placed Fake", "placed@fake.test", needsRoom: true, hotelId: hotel.Id);
        // Two need a room but are NOT placed; one does not need a room (ignored).
        await SeedPersonAsync(db, eventId, "Floating One", "f1@fake.test", needsRoom: true, hotelId: null);
        await SeedPersonAsync(db, eventId, "Floating Two", "f2@fake.test", needsRoom: true, hotelId: null);
        await SeedPersonAsync(db, eventId, "No Need", "nn@fake.test", needsRoom: false, hotelId: null);

        var snap = await blocks.BuildAsync(eventId);

        Assert.Equal(2, snap.UnassignedNeedingRoom);
        Assert.False(snap.PlanLooksOk);           // unplaced room-needers exist
        Assert.Equal(1, snap.TotalAssignedNeedingRoom);
    }

    [Fact]
    public async Task Zero_size_block_does_not_divide_by_zero()
    {
        var (blocks, mgmt, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var hotel = await mgmt.CreateHotelAsync(eventId, "Reference Hotel", null, null, null, roomBlockSize: 0);

        var snap = await blocks.BuildAsync(eventId);

        var line = Assert.Single(snap.Hotels);
        Assert.True(line.HasBlock);
        Assert.Equal(0, line.Percent);  // explicit 0, not a crash
        Assert.Equal(0, line.Remaining);
        Assert.Equal(0, line.Over);
        Assert.False(line.IsOverBlock);
    }

    [Fact]
    public async Task Snapshot_is_edition_scoped_and_hotels_are_alphabetical()
    {
        var (blocks, mgmt, db) = NewSut();
        var eventA = await SeedEventAsync(db, "AAA27");
        var eventB = await SeedEventAsync(db, "BBB27");

        await mgmt.CreateHotelAsync(eventA, "Zeta Hotel", null, null, null, roomBlockSize: 4);
        await mgmt.CreateHotelAsync(eventA, "Alpha Hotel", null, null, null, roomBlockSize: 4);
        await mgmt.CreateHotelAsync(eventB, "Other-edition Hotel", null, null, null, roomBlockSize: 9);

        var snap = await blocks.BuildAsync(eventA);

        Assert.Equal(2, snap.Hotels.Count); // event B's hotel is not included
        Assert.Equal("Alpha Hotel", snap.Hotels[0].HotelName);
        Assert.Equal("Zeta Hotel", snap.Hotels[1].HotelName);
        Assert.Equal(8, snap.TotalBlock);
    }

    [Fact]
    public async Task Negative_block_size_is_clamped_to_zero_on_create()
    {
        var (blocks, mgmt, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        await mgmt.CreateHotelAsync(eventId, "Clamp Hotel", null, null, null, roomBlockSize: -3);

        var snap = await blocks.BuildAsync(eventId);
        var line = Assert.Single(snap.Hotels);
        Assert.Equal(0, line.RoomBlockSize);
    }
}
