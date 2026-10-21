using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1355 — rating-button symbols (faces / stars / points, one or two) and §1357 — inactive responses.
/// Operator 2026-10-19: *"faces, stars (1-4), points (1-4). People can select max 2"* · *"make a state inactive so it
/// is not included … like a button"*.
/// </summary>
public sealed class RatingSymbolsAndInactiveTests
{
    private const int EventId = 1;
    private const string Token = "qr-token-symbols-aaaaaaaa";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Start = new(2027, 2, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset During = Start.AddMinutes(20);

    private sealed record World(CommunityHubDbContext Db, EvaluationRoom Room, EvaluationPhysicalRoom Physical, EvaluationDevice Kiosk);

    private static async Task<World> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), PreDayDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        var room = new EvaluationRoom { EventId = EventId, Name = "Room-18-Floor 1-Max 92", NameKey = "room-18-floor 1-max 92" };
        db.EvaluationRooms.Add(room);
        db.Sessions.Add(new Session { Id = 47, EventId = EventId, Title = "Test Session", PublicToken = Token });
        await db.SaveChangesAsync();
        db.EvaluationSessions.Add(new EvaluationSession
        {
            EventId = EventId, CehSessionId = 47, RoomId = room.Id, Title = "Test Session",
            ScheduledStart = Start, ScheduledEnd = Start.AddHours(1),
            CollectionWindowOpensAt = Start, CollectionWindowClosesAt = Start.AddMinutes(90),
        });
        var physical = new EvaluationPhysicalRoom { EventId = EventId, Name = "Room 18" };
        db.EvaluationPhysicalRooms.Add(physical);
        await db.SaveChangesAsync();
        db.EvaluationRoomPlacements.Add(new EvaluationRoomPlacement { PhysicalRoomId = physical.Id, EvaluationRoomId = room.Id });
        var kiosk = new EvaluationDevice
        {
            EventId = EventId, SerialNumber = "ELDK-HA2L06PA", PhysicalRoomId = physical.Id, IsActive = true, IsKiosk = true,
            KeyHash = EvaluationIngestService.HashKey("k"), CreatedAt = Start, UpdatedAt = Start,
        };
        db.EvaluationDevices.Add(kiosk);
        await db.SaveChangesAsync();
        return new World(db, room, physical, kiosk);
    }

    // ---- §1355 symbols --------------------------------------------------------------------------

    [Theory]
    [InlineData(1, true)]   // faces
    [InlineData(2, true)]   // stars
    [InlineData(4, true)]   // points
    [InlineData(3, true)]   // faces + stars
    [InlineData(6, true)]   // stars + points
    [InlineData(7, false)]  // three — more than two
    [InlineData(0, false)]  // none
    [InlineData(8, false)]  // unknown flag
    public void Up_to_two_known_symbols_are_valid(int value, bool valid) =>
        Assert.Equal(valid, RatingSymbolChoices.IsValid(value));

    [Fact]
    public void Stars_show_the_rating_filled_out_of_four_and_labels_read_as_words()
    {
        Assert.Equal("★★★★", RatingSymbolChoices.Stars(4));
        Assert.Equal("★☆☆☆", RatingSymbolChoices.Stars(1));
        Assert.Equal("Faces + Stars", RatingSymbolChoices.Label(RatingSymbols.Faces | RatingSymbols.Stars));
        Assert.Equal(6, RatingSymbolChoices.All.Count);
        Assert.All(RatingSymbolChoices.All, s => Assert.True(RatingSymbolChoices.IsValid((int)s)));
    }

    [Fact]
    public async Task A_kiosk_shows_faces_then_the_edition_default_then_its_own_symbols()
    {
        var w = await SeedAsync();
        Assert.Equal(RatingSymbols.Faces, await RatingSymbolChoices.ForDeviceAsync(w.Db, w.Kiosk));

        (await w.Db.Events.SingleAsync()).KioskRatingSymbols = RatingSymbols.Stars | RatingSymbols.Points;
        await w.Db.SaveChangesAsync();
        Assert.Equal(RatingSymbols.Stars | RatingSymbols.Points, await RatingSymbolChoices.ForDeviceAsync(w.Db, w.Kiosk));

        w.Kiosk.RatingSymbols = RatingSymbols.Points;
        await w.Db.SaveChangesAsync();
        Assert.Equal(RatingSymbols.Points, await RatingSymbolChoices.ForDeviceAsync(w.Db, w.Kiosk));
    }

    [Fact]
    public async Task A_QR_session_shows_its_physical_room_symbols_and_records_them()
    {
        var w = await SeedAsync();
        w.Physical.QrRatingSymbols = RatingSymbols.Faces | RatingSymbols.Stars;
        await w.Db.SaveChangesAsync();
        var qr = new EvaluationQrService(w.Db, new FixedClock(During));

        Assert.Equal(RatingSymbols.Faces | RatingSymbols.Stars, (await qr.ResolveAsync(Token)).Symbols);
        await qr.SubmitAsync(Token, 3, null, null, null);
        Assert.Equal(RatingSymbols.Faces | RatingSymbols.Stars, (await w.Db.EvaluationResponses.SingleAsync()).RatingSymbols);
    }

    [Fact]
    public async Task A_kiosk_vote_records_the_symbols_it_was_shown_else_the_ones_in_force()
    {
        var w = await SeedAsync();
        w.Kiosk.RatingSymbols = RatingSymbols.Stars;   // in force NOW
        await w.Db.SaveChangesAsync();

        await new EvaluationIngestService(w.Db, new FixedClock(During)).IngestAsync(w.Kiosk,
            [
                new EvaluationIngestService.IncomingRecord("shown-points", 4, During, Symbols: (int)RatingSymbols.Points),
                new EvaluationIngestService.IncomingRecord("old-page", 4, During),
                new EvaluationIngestService.IncomingRecord("bad-value", 4, During, Symbols: 7),
            ],
            "web-kiosk/1", During, false);

        Assert.Equal(RatingSymbols.Points, (await w.Db.EvaluationResponses.SingleAsync(r => r.DeviceRecordId == "shown-points")).RatingSymbols);
        Assert.Equal(RatingSymbols.Stars, (await w.Db.EvaluationResponses.SingleAsync(r => r.DeviceRecordId == "old-page")).RatingSymbols);
        Assert.Equal(RatingSymbols.Stars, (await w.Db.EvaluationResponses.SingleAsync(r => r.DeviceRecordId == "bad-value")).RatingSymbols);
    }

    // ---- §1357 inactive -------------------------------------------------------------------------

    private static async Task VoteAsync(World w, params string[] ids) =>
        await new EvaluationIngestService(w.Db, new FixedClock(During)).IngestAsync(w.Kiosk,
            ids.Select(id => new EvaluationIngestService.IncomingRecord(id, 4, During)).ToList(), "web-kiosk/1", During, false);

    [Fact]
    public async Task Setting_responses_inactive_takes_them_out_of_everything_and_reactivating_brings_them_back()
    {
        var w = await SeedAsync();
        await VoteAsync(w, "a", "b", "c");
        Assert.Equal(3, (await EvaluationPolicyComparison.BuildAsync(w.Db, EventId)).Sum(r => r.Responses));

        var n = await EvaluationResponseActivation.SetAllInactiveAsync(w.Db, EventId, During.AddMinutes(1));

        Assert.Equal(3, n);
        Assert.Empty(await w.Db.EvaluationResponses.ToListAsync());                          // every normal read
        Assert.Empty(await EvaluationPolicyComparison.BuildAsync(w.Db, EventId));
        Assert.Equal(new EvaluationResponseActivation.Counts(0, 3), await EvaluationResponseActivation.CountAsync(w.Db, EventId));
        Assert.Equal(3, await w.Db.EvaluationResponses.IgnoreQueryFilters().CountAsync());  // kept, not deleted

        Assert.Equal(3, await EvaluationResponseActivation.ReactivateAllAsync(w.Db, EventId));
        Assert.Equal(3, await w.Db.EvaluationResponses.CountAsync());
    }

    [Fact]
    public async Task A_vote_collected_after_the_button_stays_active()
    {
        var w = await SeedAsync();
        await VoteAsync(w, "before");
        await EvaluationResponseActivation.SetAllInactiveAsync(w.Db, EventId, During.AddMinutes(1));

        await new EvaluationIngestService(w.Db, new FixedClock(During.AddMinutes(5))).IngestAsync(w.Kiosk,
            [new EvaluationIngestService.IncomingRecord("after", 3, During.AddMinutes(4))], "web-kiosk/1", During, false);

        Assert.Equal("after", (await w.Db.EvaluationResponses.SingleAsync()).DeviceRecordId);
    }

    [Fact]
    public async Task A_retried_batch_never_brings_an_inactive_vote_back_as_a_new_one()
    {
        // The kiosk's offline queue retries whole batches; an inactive vote must stay a duplicate.
        var w = await SeedAsync();
        await VoteAsync(w, "queued-1");
        await EvaluationResponseActivation.SetAllInactiveAsync(w.Db, EventId, During.AddMinutes(1));

        var results = await new EvaluationIngestService(w.Db, new FixedClock(During.AddMinutes(2))).IngestAsync(w.Kiosk,
            [new EvaluationIngestService.IncomingRecord("queued-1", 4, During)], "web-kiosk/1", During, false);

        Assert.Equal(EvaluationIngestService.RecordStatus.Duplicate, results[0].Status);
        Assert.Equal(1, await w.Db.EvaluationResponses.IgnoreQueryFilters().CountAsync());
        Assert.Empty(await w.Db.EvaluationResponses.ToListAsync());
    }
}
