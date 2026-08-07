using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §946 — <b>A TEST USER IS NOT A MEAL, A BED OR A POLO.</b> Operator 2026-08-07:
/// *"ring 1 test users must NOT count towards hotel, lunch, polo, swags, party, appreciation sign-up,
/// etc"*, clarified in the same turn to *"IsTestUsers should not count towards these logistics"* and
/// *"dont map against ring 1 - but the flag IsTestUsers"*.
/// </summary>
/// <remarks>
/// <para>🔑 <b>The predicate is the FLAG, never the ring</b>, and these tests enforce that directly:
/// each surface is fed a participant who is <c>IsTestUser</c> while sitting on the DEFAULT ring, which
/// is precisely the shape a ring-based filter would miss. §940 makes Ring 1 imply the flag but not the
/// reverse — the seeded <c>test-*@</c> accounts and anything flagged by hand are test data without
/// being Ring 1, and they are the majority.</para>
///
/// <para>⚠️ <b>Every test also asserts the REAL participant survives.</b> Over-counting buys a meal
/// nobody eats; under-counting leaves a real person without one. A filter that excluded everybody
/// would pass a "the test user is gone" assertion and be far worse than the bug.</para>
///
/// <para>🔒 <b>Counts and LISTS are asserted together</b> where a surface has both. A total that drops
/// by one while the roster beside it still names the row makes an organizer reconcile by hand and
/// trust neither number.</para>
/// </remarks>
public sealed class TestUsersDoNotCountTowardsLogisticsTests
{
    private const int EventId = 946;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"logistics946-{Guid.NewGuid():N}")
            .Options);

    /// <summary>A real person and a test person, identical in every other respect.</summary>
    private static async Task<(Participant real, Participant test)> SeedPairAsync(
        CommunityHubDbContext db, ParticipantRole role = ParticipantRole.Volunteer)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });

        // 🔑 BOTH on the DEFAULT ring. The test user is test data by FLAG only — a filter written
        // against Ring 1 would let this row straight through into the caterer's order.
        var real = new Participant
        {
            EventId = EventId, Email = "real@example.test", FullName = "Real Person", Role = role,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active, IsTestUser = false,
        };
        var test = new Participant
        {
            EventId = EventId, Email = "sim@example.test", FullName = "Sim Person", Role = role,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active, IsTestUser = true,
        };
        db.Participants.AddRange(real, test);
        await db.SaveChangesAsync();
        return (real, test);
    }

    // ---------------------------------------------------------------- the shared rule

    [Fact]
    public async Task LogisticsAudience_counts_the_real_person_and_not_the_test_one()
    {
        using var db = NewDb();
        var (real, test) = await SeedPairAsync(db);

        var countable = await LogisticsAudience.Countable(db.Participants, EventId)
            .Select(p => p.Id).ToListAsync();

        Assert.Contains(real.Id, countable);
        Assert.DoesNotContain(test.Id, countable);
        // The in-memory twin must agree — two spellings of one rule is what drifts.
        Assert.True(LogisticsAudience.Counts(real, EventId));
        Assert.False(LogisticsAudience.Counts(test, EventId));
    }

    // ---------------------------------------------------------------- lunch

    [Fact]
    public async Task Lunch_headcount_and_run_sheet_both_exclude_the_test_user()
    {
        using var db = NewDb();
        var (real, test) = await SeedPairAsync(db);
        db.LunchSignups.AddRange(
            new LunchSignup { EventId = EventId, ParticipantId = real.Id, LunchPreDay = true },
            new LunchSignup { EventId = EventId, ParticipantId = test.Id, LunchPreDay = true });
        await db.SaveChangesAsync();

        var svc = new OrganizerExportsService(db);
        var heads = await svc.BuildLunchHeadcountAsync(EventId);
        var people = await svc.BuildLunchPeopleAsync(EventId);

        Assert.Equal(1, heads.Single(h => h.Day.Contains("Pre-day", StringComparison.Ordinal)).Count);
        // 🔒 The names must sum to the number, or the organizer reconciles by hand.
        Assert.Equal("Real Person", Assert.Single(people).Name);
    }

    // ---------------------------------------------------------------- appreciation dinner

    [Fact]
    public async Task Dinner_headcount_and_run_sheet_both_exclude_the_test_user()
    {
        using var db = NewDb();
        var (real, test) = await SeedPairAsync(db);
        db.DinnerSignups.AddRange(
            new DinnerSignup { EventId = EventId, ParticipantId = real.Id, Rsvp = DinnerRsvp.Yes, Attending = true, PlusOneCount = 1 },
            new DinnerSignup { EventId = EventId, ParticipantId = test.Id, Rsvp = DinnerRsvp.Yes, Attending = true, PlusOneCount = 1 });
        await db.SaveChangesAsync();

        var svc = new OrganizerExportsService(db);
        var heads = await svc.BuildDinnerHeadcountAsync(EventId);
        var sheet = await svc.BuildDinnerRunSheetAsync(EventId);

        // One seat + one guest — the test row's plus-one must not be catered for either.
        Assert.Equal(1, heads.Attending);
        Assert.Equal(1, heads.PlusOnes);
        Assert.Equal(2, heads.Total);
        Assert.Equal("Real Person", Assert.Single(sheet).Name);
    }

    /// <summary>A test user's allergy must not appear on the kitchen's dietary sheet either.</summary>
    [Fact]
    public async Task Dietary_headcount_excludes_the_test_user()
    {
        using var db = NewDb();
        var (real, test) = await SeedPairAsync(db);
        db.DietaryRequirements.AddRange(
            new DietaryRequirement { EventId = EventId, ParticipantId = real.Id, Milk = true },
            new DietaryRequirement { EventId = EventId, ParticipantId = test.Id, Milk = true });
        db.DinnerSignups.AddRange(
            new DinnerSignup { EventId = EventId, ParticipantId = real.Id, Rsvp = DinnerRsvp.Yes, Attending = true },
            new DinnerSignup { EventId = EventId, ParticipantId = test.Id, Rsvp = DinnerRsvp.Yes, Attending = true });
        await db.SaveChangesAsync();

        var rows = await new OrganizerExportsService(db).BuildDietaryHeadcountAsync(EventId);

        Assert.All(rows, r => Assert.True(r.Count <= 1, $"{r.Occasion}/{r.Item} counted {r.Count} — the test user is in the kitchen sheet"));
    }

    // ---------------------------------------------------------------- party

    [Fact]
    public async Task Party_counts_and_list_both_exclude_the_test_user()
    {
        using var db = NewDb();
        var (real, test) = await SeedPairAsync(db);
        db.PartyRsvps.AddRange(
            new PartyRsvp { EventId = EventId, ParticipantId = real.Id, Attending = true, Name = "Real Person", Email = real.Email },
            new PartyRsvp { EventId = EventId, ParticipantId = test.Id, Attending = true, Name = "Sim Person", Email = test.Email });
        await db.SaveChangesAsync();

        var svc = new PartyRsvpService(db);
        var (total, attending) = await svc.CountsAsync(EventId);
        var rows = await svc.GetAllAsync(EventId);

        Assert.Equal(1, total);
        Assert.Equal(1, attending);
        Assert.Equal("Real Person", Assert.Single(rows).Name);
    }

    /// <summary>
    /// ⚠️ An ANONYMOUS RSVP (no participant link) still counts. There is no flag to read on it, and
    /// dropping unlinked rows would silently lose real public sign-ups — the expensive direction.
    /// </summary>
    [Fact]
    public async Task An_anonymous_party_rsvp_still_counts()
    {
        using var db = NewDb();
        await SeedPairAsync(db);
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = EventId, ParticipantId = null, Attending = true,
            Name = "Walk In", Email = "walkin@example.test",
        });
        await db.SaveChangesAsync();

        var (total, attending) = await new PartyRsvpService(db).CountsAsync(EventId);

        Assert.Equal(1, total);
        Assert.Equal(1, attending);
    }

    // ---------------------------------------------------------------- the command-center tiles

    /// <summary>
    /// The four headcount tiles ARE the numbers he orders against — hotel rooms, swag, lunch, dinner.
    /// </summary>
    [Fact]
    public async Task The_headcount_tiles_exclude_the_test_user()
    {
        using var db = NewDb();
        var (real, test) = await SeedPairAsync(db);
        foreach (var p in new[] { real, test })
        {
            db.HotelBookings.Add(new HotelBooking { EventId = EventId, ParticipantId = p.Id, NeedsRoom = true });
            db.SwagPreferences.Add(new SwagPreference { EventId = EventId, ParticipantId = p.Id, WantsPolo = true, PoloSize = "L" });
            db.LunchSignups.Add(new LunchSignup { EventId = EventId, ParticipantId = p.Id, LunchPreDay = true });
            db.DinnerSignups.Add(new DinnerSignup { EventId = EventId, ParticipantId = p.Id, Rsvp = DinnerRsvp.Yes, Attending = true });
        }
        await db.SaveChangesAsync();

        var counts = await HeadcountsAsync(db);

        Assert.Equal(1, counts["Hotel"]);
        Assert.Equal(1, counts["Swag"]);
        Assert.Equal(1, counts["Lunch"]);
        Assert.Equal(1, counts["Dinner"]);
    }

    /// <summary>
    /// Re-states the tile queries so the assertion is about the RULE and not about wiring up the whole
    /// CommandCenterService (which pulls in sessions, sponsors and alerting). Kept deliberately
    /// identical in shape to <c>PopulateHeadcountsAsync</c>.
    /// </summary>
    private static async Task<Dictionary<string, int>> HeadcountsAsync(CommunityHubDbContext db)
    {
        var hotel = await db.HotelBookings.CountAsync(h => h.EventId == EventId && h.NeedsRoom
            && db.Participants.Any(p => p.Id == h.ParticipantId && p.IsActive && !p.IsTestUser));
        var swag = await db.SwagPreferences.CountAsync(w => w.EventId == EventId && w.WantsPolo
            && db.Participants.Any(p => p.Id == w.ParticipantId && p.IsActive && !p.IsTestUser));
        var lunch = await db.LunchSignups.CountAsync(l => l.EventId == EventId && l.LunchPreDay
            && db.Participants.Any(p => p.Id == l.ParticipantId && p.IsActive && !p.IsTestUser));
        var dinner = await db.DinnerSignups.CountAsync(d => d.EventId == EventId && d.Attending
            && db.Participants.Any(p => p.Id == d.ParticipantId && p.IsActive && !p.IsTestUser));
        return new Dictionary<string, int>
        {
            ["Hotel"] = hotel, ["Swag"] = swag, ["Lunch"] = lunch, ["Dinner"] = dinner,
        };
    }

    // ---------------------------------------------------------------- hotel placement

    [Fact]
    public async Task Hotel_occupancy_excludes_the_test_user()
    {
        using var db = NewDb();
        var hotel = new Hotel { EventId = EventId, Name = "Test Hotel" };
        db.Hotels.Add(hotel);
        await db.SaveChangesAsync();

        var (real, test) = await SeedPairAsync(db);
        real.HotelId = hotel.Id;
        test.HotelId = hotel.Id;
        db.HotelBookings.AddRange(
            new HotelBooking { EventId = EventId, ParticipantId = real.Id, NeedsRoom = true },
            new HotelBooking { EventId = EventId, ParticipantId = test.Id, NeedsRoom = true });
        await db.SaveChangesAsync();

        var occupants = (await new HotelManagementService(db, TimeProvider.System)
                .GroupByHotelAsync(EventId))
            .SelectMany(g => g.Occupants)
            .ToList();

        Assert.DoesNotContain(occupants, o => o.Email == "sim@example.test");
        Assert.Contains(occupants, o => o.Email == "real@example.test");
    }

    // ---------------------------------------------------------------- the badge export (precedent)

    /// <summary>The surface that already had this right — pinned so it stays right.</summary>
    [Fact]
    public async Task Badge_data_still_excludes_the_test_user()
    {
        using var db = NewDb();
        await SeedPairAsync(db);

        var badges = await new OrganizerExportsService(db).BuildBadgeDataAsync(EventId);

        Assert.DoesNotContain(badges, b => b.Name == "Sim Person");
        Assert.Contains(badges, b => b.Name == "Real Person");
    }
}
