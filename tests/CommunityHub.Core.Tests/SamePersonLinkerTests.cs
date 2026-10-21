using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1312 — "Link selected as one person" on the participants list, primary picked automatically (operator 2026-09-27:
/// "easier to have a link button where i made the view"). The primary is the row that loses nothing — the speaker row holding
/// the bed, even when the empty sponsor row is older — and a selection nobody can represent is refused, never guessed.
/// </summary>
public sealed class SamePersonLinkerTests
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
            new Participant { Id = 19, EventId = EventId, FullName = "Ronni Pedersen", Email = "sponsor@x.dk", Role = ParticipantRole.Sponsor, IsActive = true },
            new Participant { Id = 112, EventId = EventId, FullName = "Ronni Pedersen", Email = "speaker@x.dk", Role = ParticipantRole.Speaker, IsActive = true },
            new Participant { Id = 130, EventId = EventId, FullName = "Ronni P", Email = "volunteer@x.dk", Role = ParticipantRole.Volunteer, IsActive = true });
        db.HotelBookings.Add(new HotelBooking { EventId = EventId, ParticipantId = 112, NeedsRoom = true });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task The_row_holding_everything_becomes_the_primary_automatically()
    {
        using var db = await SeedAsync();

        var r = await SamePersonLinker.LinkAsync(db, EventId, new[] { 19, 112, 130 });

        Assert.True(r.Ok, r.Message);
        Assert.Equal(112, r.PrimaryId);                       // not the oldest (19): it would drop the hotel booking
        var rows = await db.Participants.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(112, rows.Single(p => p.Id == 19).SamePersonAsId);
        Assert.Equal(112, rows.Single(p => p.Id == 130).SamePersonAsId);
        Assert.Null(rows.Single(p => p.Id == 112).SamePersonAsId);
    }

    [Fact]
    public async Task A_selection_nobody_can_represent_is_refused_and_says_why()
    {
        using var db = await SeedAsync();
        db.SwagPreferences.Add(new SwagPreference { EventId = EventId, ParticipantId = 19 });   // each holds something the other lacks
        await db.SaveChangesAsync();

        var r = await SamePersonLinker.LinkAsync(db, EventId, new[] { 19, 112 });

        Assert.False(r.Ok);
        Assert.Contains("No ticked row can be the primary", r.Message);
        Assert.All(await db.Participants.AsNoTracking().ToListAsync(), p => Assert.Null(p.SamePersonAsId));
    }

    [Fact]
    public async Task One_row_or_a_row_linked_outside_the_selection_is_refused()
    {
        using var db = await SeedAsync();
        Assert.False((await SamePersonLinker.LinkAsync(db, EventId, new[] { 112 })).Ok);

        db.Participants.Single(p => p.Id == 130).SamePersonAsId = 112;
        await db.SaveChangesAsync();
        var r = await SamePersonLinker.LinkAsync(db, EventId, new[] { 19, 130 });
        Assert.False(r.Ok);
        Assert.Contains("not ticked", r.Message);
    }
}
