using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="HotelBulkOperationService"/> — organizer bulk
/// delete over a multi-selected set of hotels (REQUIREMENTS §20 universal CRUD +
/// bulk). Mirrors the single-row delete's safe semantics batch-wide: every placed
/// participant is un-assigned first so no FK dangles, edition scope is honoured,
/// and the result reports deleted / un-assigned / not-found counts honestly.
/// EF Core InMemory + a fixed clock; FAKE hotel + person names only.
/// </summary>
public sealed class HotelBulkOperationServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);

    private static (HotelBulkOperationService bulk, HotelManagementService mgmt, CommunityHubDbContext db) NewSut()
    {
        var db = TestDb.New();
        return (new HotelBulkOperationService(db), new HotelManagementService(db, new FixedClock(T0)), db);
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
        CommunityHubDbContext db, int eventId, string name, string email, int? hotelId = null)
    {
        var p = new Participant
        {
            EventId = eventId, FullName = name, Email = email,
            Role = ParticipantRole.Volunteer, IsActive = true, HotelId = hotelId,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task Empty_selection_is_a_no_op()
    {
        var (bulk, _, db) = NewSut();
        var eventId = await SeedEventAsync(db);

        var result = await bulk.DeleteAsync(eventId, Array.Empty<int>());

        Assert.Equal(0, result.Matched);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Unassigned);
    }

    [Fact]
    public async Task Deletes_all_selected_hotels_in_one_call()
    {
        var (bulk, mgmt, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var h1 = await mgmt.CreateHotelAsync(eventId, "Alpha Hotel", null, null, null);
        var h2 = await mgmt.CreateHotelAsync(eventId, "Beta Hotel", null, null, null);
        var keep = await mgmt.CreateHotelAsync(eventId, "Keep Hotel", null, null, null);

        var result = await bulk.DeleteAsync(eventId, new[] { h1.Id, h2.Id });

        Assert.Equal(2, result.Matched);
        Assert.Equal(2, result.Deleted);
        var left = await mgmt.ListHotelsAsync(eventId);
        var only = Assert.Single(left);
        Assert.Equal(keep.Id, only.Id);
    }

    [Fact]
    public async Task Unassigns_placed_participants_before_removing_and_counts_them()
    {
        var (bulk, mgmt, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var doomed = await mgmt.CreateHotelAsync(eventId, "Doomed Hotel", null, null, null);
        var p1 = await SeedPersonAsync(db, eventId, "Ada Fake", "ada@fake.test", doomed.Id);
        var p2 = await SeedPersonAsync(db, eventId, "Bo Fake", "bo@fake.test", doomed.Id);

        var result = await bulk.DeleteAsync(eventId, new[] { doomed.Id });

        Assert.Equal(1, result.Deleted);
        Assert.Equal(2, result.Unassigned);
        Assert.Empty(await mgmt.ListHotelsAsync(eventId));
        // Participant records are kept, just un-assigned (not orphaned, not deleted).
        Assert.Null((await db.Participants.FirstAsync(p => p.Id == p1)).HotelId);
        Assert.Null((await db.Participants.FirstAsync(p => p.Id == p2)).HotelId);
    }

    [Fact]
    public async Task Hotels_from_another_edition_are_never_touched()
    {
        var (bulk, mgmt, db) = NewSut();
        var eventA = await SeedEventAsync(db, "AAA27");
        var eventB = await SeedEventAsync(db, "BBB27");
        var hotelB = await mgmt.CreateHotelAsync(eventB, "Other-edition hotel", null, null, null);

        // Caller is in edition A but tries to delete edition B's hotel id.
        var result = await bulk.DeleteAsync(eventA, new[] { hotelB.Id });

        Assert.Equal(0, result.Matched);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Skipped(requested: 1));
        Assert.Single(await mgmt.ListHotelsAsync(eventB)); // still there
    }

    [Fact]
    public async Task Unknown_ids_are_skipped_not_errored()
    {
        var (bulk, mgmt, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var real = await mgmt.CreateHotelAsync(eventId, "Real Hotel", null, null, null);

        var result = await bulk.DeleteAsync(eventId, new[] { real.Id, 9999 });

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Skipped(requested: 2));
    }

    [Fact]
    public async Task Duplicate_and_nonpositive_ids_do_not_widen_the_match()
    {
        var (bulk, mgmt, db) = NewSut();
        var eventId = await SeedEventAsync(db);
        var real = await mgmt.CreateHotelAsync(eventId, "Real Hotel", null, null, null);

        var result = await bulk.DeleteAsync(eventId, new[] { real.Id, real.Id, 0, -1 });

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Deleted);
    }
}
