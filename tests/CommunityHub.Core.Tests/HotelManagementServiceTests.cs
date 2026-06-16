using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="HotelManagementService"/> — multi-hotel
/// management (REQUIREMENTS §3): hotel CRUD, assigning participants to hotels,
/// the per-person confirmation number, and the group-by-hotel view.
/// EF Core InMemory + a fixed clock; FAKE hotel + person names only.
/// </summary>
public sealed class HotelManagementServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);

    private static (HotelManagementService svc, CommunityHubDbContext db) NewSut()
    {
        var db = TestDb.New();
        return (new HotelManagementService(db, new FixedClock(T0)), db);
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
        ParticipantRole role = ParticipantRole.Volunteer)
    {
        var p = new Participant
        {
            EventId = eventId, FullName = name, Email = email,
            Role = role, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task Create_then_list_returns_the_hotel()
    {
        var (svc, db) = NewSut();
        var eventId = await SeedEventAsync(db);

        var hotel = await svc.CreateHotelAsync(
            eventId, "Central Plaza Hotel", "1 Main Street, Springfield", "front@plaza.test", "Room block A");

        Assert.True(hotel.Id > 0);
        var list = await svc.ListHotelsAsync(eventId);
        var only = Assert.Single(list);
        Assert.Equal("Central Plaza Hotel", only.Name);
        Assert.Equal("1 Main Street, Springfield", only.Address);
        Assert.Equal("front@plaza.test", only.ContactEmail);
    }

    [Fact]
    public async Task Create_with_blank_name_throws()
    {
        var (svc, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.CreateHotelAsync(eventId, "   ", null, null, null));
    }

    [Fact]
    public async Task Update_changes_fields_in_place()
    {
        var (svc, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var hotel = await svc.CreateHotelAsync(eventId, "Old Name", "Old Addr", null, null);

        var ok = await svc.UpdateHotelAsync(eventId, hotel.Id, "New Name", "New Addr", "x@y.test", "note");

        Assert.True(ok);
        var reloaded = await svc.GetHotelAsync(eventId, hotel.Id);
        Assert.Equal("New Name", reloaded!.Name);
        Assert.Equal("New Addr", reloaded.Address);
        Assert.NotNull(reloaded.UpdatedAt);
    }

    [Fact]
    public async Task Delete_unassigns_placed_participants_then_removes()
    {
        var (svc, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var hotel = await svc.CreateHotelAsync(eventId, "Doomed Hotel", null, null, null);
        var pid = await SeedPersonAsync(db, eventId, "Ada Fake", "ada@fake.test");
        Assert.True(await svc.AssignParticipantAsync(eventId, pid, hotel.Id));

        var ok = await svc.DeleteHotelAsync(eventId, hotel.Id);

        Assert.True(ok);
        Assert.Empty(await svc.ListHotelsAsync(eventId));
        var person = await db.Participants.FirstAsync(p => p.Id == pid);
        Assert.Null(person.HotelId); // un-assigned, not orphaned
    }

    [Fact]
    public async Task Assign_places_participant_and_clear_removes_placement()
    {
        var (svc, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var hotel = await svc.CreateHotelAsync(eventId, "Plaza", null, null, null);
        var pid = await SeedPersonAsync(db, eventId, "Bo Fake", "bo@fake.test");

        Assert.True(await svc.AssignParticipantAsync(eventId, pid, hotel.Id));
        Assert.Equal(hotel.Id, (await db.Participants.FirstAsync(p => p.Id == pid)).HotelId);

        Assert.True(await svc.AssignParticipantAsync(eventId, pid, null));
        Assert.Null((await db.Participants.FirstAsync(p => p.Id == pid)).HotelId);
    }

    [Fact]
    public async Task Assign_to_hotel_from_another_edition_is_rejected()
    {
        var (svc, db) = NewSut();
        var eventA = await SeedEventAsync(db, "AAA27");
        var eventB = await SeedEventAsync(db, "BBB27");
        var hotelB = await svc.CreateHotelAsync(eventB, "Other-edition hotel", null, null, null);
        var pidA = await SeedPersonAsync(db, eventA, "Cy Fake", "cy@fake.test");

        var ok = await svc.AssignParticipantAsync(eventA, pidA, hotelB.Id);

        Assert.False(ok);
        Assert.Null((await db.Participants.FirstAsync(p => p.Id == pidA)).HotelId);
    }

    [Fact]
    public async Task SetConfirmationNumber_persists_and_clears()
    {
        var (svc, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var pid = await SeedPersonAsync(db, eventId, "Di Fake", "di@fake.test");

        Assert.True(await svc.SetConfirmationNumberAsync(eventId, pid, "CONF-12345"));
        Assert.Equal("CONF-12345", (await db.Participants.FirstAsync(p => p.Id == pid)).HotelConfirmationNumber);

        Assert.True(await svc.SetConfirmationNumberAsync(eventId, pid, "   "));
        Assert.Null((await db.Participants.FirstAsync(p => p.Id == pid)).HotelConfirmationNumber);
    }

    [Fact]
    public async Task GroupByHotel_buckets_people_and_adds_not_assigned_group()
    {
        var (svc, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var h1 = await svc.CreateHotelAsync(eventId, "Alpha Hotel", "Addr A", null, null);
        var h2 = await svc.CreateHotelAsync(eventId, "Beta Hotel", "Addr B", null, null);

        var p1 = await SeedPersonAsync(db, eventId, "Zoe Fake", "zoe@fake.test");
        var p2 = await SeedPersonAsync(db, eventId, "Amy Fake", "amy@fake.test");
        var p3 = await SeedPersonAsync(db, eventId, "Unplaced Fake", "un@fake.test");

        await svc.AssignParticipantAsync(eventId, p1, h1.Id);
        await svc.AssignParticipantAsync(eventId, p2, h1.Id);
        await svc.SetConfirmationNumberAsync(eventId, p1, "RES-1");
        // p3 left unassigned.

        var groups = await svc.GroupByHotelAsync(eventId);

        // Alpha, Beta (alphabetical), then "Not assigned" (Hotel == null) last.
        Assert.Equal(3, groups.Count);
        Assert.Equal("Alpha Hotel", groups[0].Hotel!.Name);
        Assert.Equal("Beta Hotel", groups[1].Hotel!.Name);
        Assert.Null(groups[2].Hotel);

        // Alpha holds both placed people, sorted by name (Amy before Zoe).
        var alpha = groups[0];
        Assert.Equal(2, alpha.Count);
        Assert.Equal("Amy Fake", alpha.Occupants[0].FullName);
        Assert.Equal("Zoe Fake", alpha.Occupants[1].FullName);
        Assert.Equal(1, alpha.Confirmed); // only p1 has a confirmation number

        // Beta is an empty hotel and still appears (count 0).
        Assert.Equal(0, groups[1].Count);

        // The unassigned group carries p3.
        var unassigned = groups[2];
        var occ = Assert.Single(unassigned.Occupants);
        Assert.Equal("Unplaced Fake", occ.FullName);
    }

    [Fact]
    public async Task GroupByHotel_surfaces_room_need_from_booking()
    {
        var (svc, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var pid = await SeedPersonAsync(db, eventId, "Eli Fake", "eli@fake.test");
        db.HotelBookings.Add(new HotelBooking
        {
            EventId = eventId, ParticipantId = pid, NeedsRoom = true,
        });
        await db.SaveChangesAsync();

        var groups = await svc.GroupByHotelAsync(eventId);
        var occ = Assert.Single(groups.Single(g => g.Hotel is null).Occupants);
        Assert.True(occ.NeedsRoom);
    }
}
