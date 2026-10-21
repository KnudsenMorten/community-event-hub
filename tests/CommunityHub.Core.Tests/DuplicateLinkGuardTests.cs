using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1257b — linking two rows as one human must never quietly remove something the event has
/// already promised somebody.
///
/// <para>Written from the live case: Ronni Pedersen's SPEAKER row holds the hotel booking, swag,
/// lunch and dinner; his SPONSOR row holds none of them. Since §1257 a linked row is excluded from
/// every logistics file, so linking the speaker row would take a real bed out of the hotel order
/// while the person still arrives expecting it. The only linking control that existed
/// (<c>SpeakerReview</c>) can only make the SPEAKER row the duplicate — i.e. exactly that mistake.</para>
/// </summary>
public class DuplicateLinkGuardTests
{
    private const int EventId = 1;

    private static async Task<CommunityHub.Core.Data.CommunityHubDbContext> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.Participants.AddRange(
            new Participant { Id = 112, EventId = EventId, FullName = "Ronni Pedersen", Email = "speaker@x.dk", Role = ParticipantRole.Speaker, IsActive = true },
            new Participant { Id = 19, EventId = EventId, FullName = "Ronni Pedersen", Email = "sponsor@x.dk", Role = ParticipantRole.Sponsor, IsActive = true });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task GiveSpeakerTheLogisticsAsync(CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        db.HotelBookings.Add(new HotelBooking { EventId = EventId, ParticipantId = 112, NeedsRoom = true });
        db.SwagPreferences.Add(new SwagPreference { EventId = EventId, ParticipantId = 112 });
        db.LunchSignups.Add(new LunchSignup { EventId = EventId, ParticipantId = 112 });
        db.DinnerSignups.Add(new DinnerSignup { EventId = EventId, ParticipantId = 112 });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Linking_the_row_that_holds_the_bed_is_REFUSED_and_says_what_would_be_lost()
    {
        using var db = await SeedAsync();
        await GiveSpeakerTheLogisticsAsync(db);

        // The wrong direction: make the SPEAKER (who holds everything) the duplicate.
        var lost = await DuplicateLinkGuard.WouldLoseAsync(db, EventId, duplicateId: 112, primaryId: 19);

        Assert.NotEmpty(lost);
        var message = DuplicateLinkGuard.Refusal(112, "Ronni Pedersen", 19, "Ronni Pedersen", lost);
        Assert.Contains("hotel booking", message);
        Assert.Contains("Not linked", message);
        // 🔑 A refusal without a next step is a wall — the message must name the way out.
        Assert.Contains("the other way round", message);
    }

    [Fact]
    public async Task Linking_the_EMPTY_row_is_allowed()
    {
        using var db = await SeedAsync();
        await GiveSpeakerTheLogisticsAsync(db);

        // The right direction: the sponsor row holds nothing, so nothing leaves the order.
        var lost = await DuplicateLinkGuard.WouldLoseAsync(db, EventId, duplicateId: 19, primaryId: 112);

        Assert.Empty(lost);
    }

    [Fact]
    public async Task An_item_BOTH_rows_hold_is_not_a_loss()
    {
        // The primary already answers for it, so the count is unchanged by the link.
        using var db = await SeedAsync();
        db.LunchSignups.AddRange(
            new LunchSignup { EventId = EventId, ParticipantId = 112 },
            new LunchSignup { EventId = EventId, ParticipantId = 19 });
        await db.SaveChangesAsync();

        var lost = await DuplicateLinkGuard.WouldLoseAsync(db, EventId, duplicateId: 19, primaryId: 112);

        Assert.Empty(lost);
    }

    [Fact]
    public async Task Two_rows_holding_NOTHING_link_freely()
    {
        using var db = await SeedAsync();

        Assert.Empty(await DuplicateLinkGuard.WouldLoseAsync(db, EventId, duplicateId: 19, primaryId: 112));
        Assert.Empty(await DuplicateLinkGuard.WouldLoseAsync(db, EventId, duplicateId: 112, primaryId: 19));
    }

    [Fact]
    public async Task A_hotel_booking_that_does_NOT_need_a_room_is_not_a_loss()
    {
        // 🔒 The row exists for every person who answered the form — including "no thanks".
        // Treating that as a lost bed would refuse links for no reason and teach the operator
        // to ignore the guard.
        using var db = await SeedAsync();
        db.HotelBookings.Add(new HotelBooking { EventId = EventId, ParticipantId = 19, NeedsRoom = false });
        await db.SaveChangesAsync();

        Assert.Empty(await DuplicateLinkGuard.WouldLoseAsync(db, EventId, duplicateId: 19, primaryId: 112));
    }
}
