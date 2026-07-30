using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §326bs — per-night hotel allotment vs demand. The numbers here decide what the event
/// contracts and pays for, so the seed plants every way a night could be counted wrong:
/// a DROP-OUT with a live booking, a booking that spans the check-out boundary, a person
/// who needs a room but has no hotel yet, a booking with missing dates, and another
/// edition's rows. FAKE names only (public-mirror safety).
/// </summary>
public sealed class HotelAllotmentServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private static readonly DateOnly N5 = new(2027, 2, 5);
    private static readonly DateOnly N6 = new(2027, 2, 6);
    private static readonly DateOnly N7 = new(2027, 2, 7);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-07-25T10:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"allot-{Guid.NewGuid():N}")
            .Options);

    private static HotelAllotmentService NewSvc(CommunityHubDbContext db) => new(db, new FixedClock());

    private sealed record Seeded(int BellaId, int OtherHotelId);

    private static async Task<Seeded> SeedAsync(CommunityHubDbContext db)
    {
        db.Events.AddRange(
            new Event
            {
                Id = EventId, Code = "AL27", CommunityName = "Allot", DisplayName = "Allot 2027",
                StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
            },
            new Event
            {
                Id = OtherEventId, Code = "OTHER", CommunityName = "Other", DisplayName = "Other",
                StartDate = new DateOnly(2027, 5, 1), EndDate = new DateOnly(2027, 5, 2), IsActive = false,
            });

        var bella = new Hotel { EventId = EventId, Name = "AC Hotel Alpha" };
        var second = new Hotel { EventId = EventId, Name = "Hotel Bravo" };
        db.Hotels.AddRange(bella, second);
        await db.SaveChangesAsync();

        Participant P(string name, bool active, int? hotelId)
        {
            var p = new Participant
            {
                EventId = EventId, FullName = name, Email = $"{name.Replace(' ', '.')}@example.test",
                Role = ParticipantRole.Speaker, IsActive = active,
                LifecycleState = ParticipantLifecycleState.Active, HotelId = hotelId,
            };
            db.Participants.Add(p);
            return p;
        }

        var alice = P("Alice Alpha", true, bella.Id);        // 5→7: two nights
        var bob = P("Bob Bravo", true, bella.Id);            // 5→6: one night
        var carol = P("Carol Charlie", true, second.Id);     // 6→7 at the other hotel
        var dave = P("Dave Delta", false, bella.Id);         // DROP-OUT, must not count
        var erin = P("Erin Echo", true, null);               // needs a room, not placed yet
        var frank = P("Frank Foxtrot", true, bella.Id);      // no dates -> uncountable
        await db.SaveChangesAsync();

        void Book(int pid, DateOnly? inD, DateOnly? outD, bool needs = true)
            => db.HotelBookings.Add(new HotelBooking
            {
                EventId = EventId, ParticipantId = pid, NeedsRoom = needs,
                CheckInDate = inD, CheckOutDate = outD,
            });

        Book(alice.Id, N5, N7);
        Book(bob.Id, N5, N6);
        Book(carol.Id, N6, N7);
        Book(dave.Id, N5, N7);            // drop-out
        Book(erin.Id, N5, N6);            // unplaced
        Book(frank.Id, null, null);       // no dates

        // Contracted supply at the first hotel: 5th = 2, 6th = 1. The 7th is NOT contracted.
        db.HotelAllotments.AddRange(
            new HotelAllotment { EventId = EventId, HotelId = bella.Id, Night = N5, RoomsAllotted = 2 },
            new HotelAllotment { EventId = EventId, HotelId = bella.Id, Night = N6, RoomsAllotted = 1 });

        await db.SaveChangesAsync();
        return new Seeded(bella.Id, second.Id);
    }

    [Fact]
    public async Task Demand_counts_every_night_of_a_stay_but_not_the_checkout_day()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);

        var board = await NewSvc(db).BuildAsync(EventId);
        var bella = board.Rows.Single(r => r.HotelId == s.BellaId);

        // Alice 5→7 covers the 5th and 6th; Bob 5→6 covers only the 5th.
        Assert.Equal(2, bella.Cells.Single(c => c.Night == N5).Demand);
        Assert.Equal(1, bella.Cells.Single(c => c.Night == N6).Demand);

        // The 7th is nobody's night — it is only a CHECK-OUT day, and nothing is
        // contracted for it — so it is not even a column. Columns are exactly the nights
        // that are contracted or requested; an empty column would invite ordering for it.
        Assert.Equal(new[] { N5, N6 }, board.Nights);
        Assert.DoesNotContain(N7, board.Nights);
    }

    [Fact]
    public async Task A_deactivated_participants_booking_never_counts()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);

        var board = await NewSvc(db).BuildAsync(EventId);
        var bella = board.Rows.Single(r => r.HotelId == s.BellaId);

        // Dave's 5→7 booking survives his deactivation; if it counted, the 5th would be 3.
        Assert.Equal(2, bella.Cells.Single(c => c.Night == N5).Demand);
    }

    [Fact]
    public async Task Unplaced_demand_gets_its_own_row_rather_than_disappearing()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var board = await NewSvc(db).BuildAsync(EventId);
        var unassigned = board.Rows.Single(r => r.IsUnassigned);

        Assert.Equal(HotelAllotmentService.UnassignedRowName, unassigned.HotelName);
        Assert.Equal(1, unassigned.Cells.Single(c => c.Night == N5).Demand);   // Erin
        // …and the whole-event demand INCLUDES her, so the totals can never look balanced
        // while somebody still has no bed. 5th = Alice + Bob (placed) + Erin (unplaced).
        Assert.Equal(3, board.DemandOn(N5));
        Assert.Equal(2, board.AllottedOn(N5));
        Assert.Equal(-1, board.VarianceOn(N5));
        Assert.Contains(N5, board.ShortNights);
    }

    [Fact]
    public async Task Variance_flags_short_and_slack_nights()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);

        var board = await NewSvc(db).BuildAsync(EventId);
        var bella = board.Rows.Single(r => r.HotelId == s.BellaId);

        var fifth = bella.Cells.Single(c => c.Night == N5);
        Assert.Equal(0, fifth.Variance);          // 2 held, 2 needed
        Assert.False(fifth.IsShort);
        Assert.False(fifth.HasSlack);

        var sixth = bella.Cells.Single(c => c.Night == N6);
        Assert.Equal(0, sixth.Variance);          // 1 held, 1 needed (Alice)

        // Hotel Bravo has demand but NO contract row: "not contracted" is not the same as
        // a contracted 0, and it must not report a variance at all.
        var bravo = board.Rows.Single(r => r.HotelId == s.OtherHotelId);
        var bravoSixth = bravo.Cells.Single(c => c.Night == N6);
        Assert.True(bravoSixth.NotContracted);
        Assert.Null(bravoSixth.Variance);
        Assert.Equal(1, bravoSixth.Demand);       // Carol is there, with nothing held for her
    }

    [Fact]
    public async Task Slack_appears_when_a_booking_is_withdrawn()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);

        // Bob no longer needs a room: the 5th now holds 2 for 1 person.
        var bob = await db.Participants.SingleAsync(p => p.FullName == "Bob Bravo");
        var booking = await db.HotelBookings.SingleAsync(b => b.ParticipantId == bob.Id);
        booking.NeedsRoom = false;
        await db.SaveChangesAsync();

        var board = await NewSvc(db).BuildAsync(EventId);
        var fifth = board.Rows.Single(r => r.HotelId == s.BellaId).Cells.Single(c => c.Night == N5);

        Assert.True(fifth.HasSlack);
        Assert.Equal(1, fifth.Variance);

        // The WHOLE-EVENT view still reads level, not slack: unplaced Erin needs a room on
        // the 5th, so the spare one at this hotel is not really spare until she is placed.
        // That is the point of counting unplaced demand in the totals.
        Assert.Equal(0, board.VarianceOn(N5));
        Assert.DoesNotContain(N5, board.SlackNights);
    }

    [Fact]
    public async Task Short_night_is_reported_when_demand_exceeds_the_contract()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);

        await NewSvc(db).SetAllotmentsAsync(EventId, s.BellaId, new Dictionary<DateOnly, int?> { [N5] = 1 });

        var board = await NewSvc(db).BuildAsync(EventId);
        var fifth = board.Rows.Single(r => r.HotelId == s.BellaId).Cells.Single(c => c.Night == N5);

        Assert.True(fifth.IsShort);
        Assert.Equal(-1, fifth.Variance);
        Assert.Contains(N5, board.ShortNights);
    }

    [Fact]
    public async Task Setting_null_clears_the_contract_while_zero_records_no_rooms_held()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        var svc = NewSvc(db);

        await svc.SetAllotmentsAsync(EventId, s.BellaId, new Dictionary<DateOnly, int?>
        {
            [N5] = null,   // remove from the contract entirely
            [N6] = 0,      // contracted, zero rooms held
        });

        var board = await svc.BuildAsync(EventId);
        var bella = board.Rows.Single(r => r.HotelId == s.BellaId);

        Assert.True(bella.Cells.Single(c => c.Night == N5).NotContracted);
        Assert.Equal(0, bella.Cells.Single(c => c.Night == N6).Allotted);
        Assert.False(bella.Cells.Single(c => c.Night == N6).NotContracted);
    }

    [Fact]
    public async Task Negative_input_is_clamped_to_zero_never_treated_as_unlimited()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        var svc = NewSvc(db);

        await svc.SetAllotmentsAsync(EventId, s.BellaId, new Dictionary<DateOnly, int?> { [N5] = -4 });

        var board = await svc.BuildAsync(EventId);
        Assert.Equal(0, board.Rows.Single(r => r.HotelId == s.BellaId)
            .Cells.Single(c => c.Night == N5).Allotted);
    }

    [Fact]
    public async Task Set_range_fills_every_night_inclusive_and_upserts()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        var svc = NewSvc(db);

        var changed = await svc.SetRangeAsync(EventId, s.BellaId, N5, N7, 9);

        Assert.Equal(3, changed);
        var bella = (await svc.BuildAsync(EventId)).Rows.Single(r => r.HotelId == s.BellaId);
        Assert.All(bella.Cells.Where(c => c.Night <= N7), c => Assert.Equal(9, c.Allotted));
    }

    [Fact]
    public async Task Allotments_are_scoped_to_their_edition_and_hotel()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        var svc = NewSvc(db);

        // A hotel that belongs to another edition is refused outright.
        var ghost = new Hotel { EventId = OtherEventId, Name = "Ghost Hotel" };
        db.Hotels.Add(ghost);
        await db.SaveChangesAsync();

        var changed = await svc.SetAllotmentsAsync(EventId, ghost.Id, new Dictionary<DateOnly, int?> { [N5] = 5 });
        Assert.Equal(0, changed);

        var board = await svc.BuildAsync(EventId);
        Assert.DoesNotContain(board.Rows, r => r.HotelName == "Ghost Hotel");
        // The second in-edition hotel has no contract, but does carry Carol's demand.
        var other = board.Rows.Single(r => r.HotelId == s.OtherHotelId);
        Assert.Equal(1, other.Cells.Single(c => c.Night == N6).Demand);
    }

    [Fact]
    public async Task Cutoffs_upsert_on_hotel_plus_date_and_clamp_the_percentage()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        var svc = NewSvc(db);
        var when = new DateOnly(2026, 12, 7);

        Assert.True(await svc.SaveCutoffAsync(EventId, s.BellaId, when, "Free cancellation", 100, null));
        Assert.True(await svc.SaveCutoffAsync(EventId, s.BellaId, when, "Free cancellation deadline", 250, "clause 4"));

        var all = await svc.ListCutoffsAsync(EventId);
        var only = Assert.Single(all);
        Assert.Equal("Free cancellation deadline", only.Label);
        Assert.Equal(100, only.ReleasePercent);      // clamped from 250
        Assert.Equal("clause 4", only.Notes);

        // A blank label is refused — the label IS the contract rule.
        Assert.False(await svc.SaveCutoffAsync(EventId, s.BellaId, when.AddDays(1), "  ", 20, null));

        Assert.True(await svc.DeleteCutoffAsync(EventId, only.Id));
        Assert.Empty(await svc.ListCutoffsAsync(EventId));
    }

    [Fact]
    public async Task An_empty_edition_yields_an_empty_board()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "AL27", CommunityName = "Allot", DisplayName = "Allot 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();

        var board = await NewSvc(db).BuildAsync(EventId);

        Assert.True(board.IsEmpty);
        Assert.Empty(board.Nights);
    }
}
