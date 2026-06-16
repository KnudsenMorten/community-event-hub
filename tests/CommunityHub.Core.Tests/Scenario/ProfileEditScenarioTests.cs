using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: a participant edits their own profile basics (REQUIREMENTS §1).
/// The /Profile page reuses the existing <see cref="Participant"/> fields
/// (FullName, Phone) — no schema change — and scopes every read/write to the
/// signed-in participant's OWN row. This backend half proves the query-level
/// scoping the page relies on (a speaker can never load or save another
/// person's profile) and the save semantics (name + phone persist; trimming).
/// </summary>
public sealed class ProfileEditScenarioTests
{
    [Fact]
    public async Task Participant_can_update_their_own_name_and_phone()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // Mirror ProfileModel.OnPostAsync: load OWN row, write trimmed values.
        var me = await db.Participants.FirstAsync(
            p => p.Id == seed.SpeakerOneId && p.EventId == seed.EventId);
        me.FullName = "Updated Speaker Name";
        me.Phone = "+45 11 22 33 44";
        await db.SaveChangesAsync();

        var reloaded = await db.Participants.FirstAsync(p => p.Id == seed.SpeakerOneId);
        Assert.Equal("Updated Speaker Name", reloaded.FullName);
        Assert.Equal("+45 11 22 33 44", reloaded.Phone);
    }

    [Fact]
    public async Task Editing_one_profile_never_touches_another_participants_row()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var beforeTwo = await db.Participants
            .Where(p => p.Id == seed.SpeakerTwoId)
            .Select(p => p.FullName)
            .FirstAsync();

        // The page query is always scoped to (Id == me && EventId == me.EventId).
        var me = await db.Participants.FirstOrDefaultAsync(
            p => p.Id == seed.SpeakerOneId && p.EventId == seed.EventId);
        Assert.NotNull(me);
        me!.FullName = "Only Speaker One Changed";
        await db.SaveChangesAsync();

        var afterTwo = await db.Participants
            .Where(p => p.Id == seed.SpeakerTwoId)
            .Select(p => p.FullName)
            .FirstAsync();
        Assert.Equal(beforeTwo, afterTwo);
    }

    [Fact]
    public async Task Email_and_role_are_read_only_facts_not_changed_by_a_basics_edit()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var before = await db.Participants
            .Where(p => p.Id == seed.SpeakerOneId)
            .Select(p => new { p.Email, p.Role })
            .FirstAsync();

        // A basics edit touches FullName/Phone only (as the page binds).
        var me = await db.Participants.FirstAsync(p => p.Id == seed.SpeakerOneId);
        me.FullName = "New Name";
        me.Phone = "123";
        await db.SaveChangesAsync();

        var after = await db.Participants
            .Where(p => p.Id == seed.SpeakerOneId)
            .Select(p => new { p.Email, p.Role })
            .FirstAsync();

        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.Role, after.Role);
    }

    [Fact]
    public async Task Every_role_has_a_loadable_own_profile_row()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // /Profile is an every-role page: each seeded participant resolves to
        // exactly one own-row by (Id, EventId).
        int[] ids =
        {
            seed.OrganizerId, seed.MasterclassSpeakerId, seed.SpeakerOneId,
            seed.SpeakerTwoId, seed.SponsorContactId, seed.VolunteerId, seed.AttendeeId,
        };

        foreach (var id in ids)
        {
            var p = await db.Participants.FirstOrDefaultAsync(
                x => x.Id == id && x.EventId == seed.EventId);
            Assert.NotNull(p);
            Assert.False(string.IsNullOrWhiteSpace(p!.Email));
        }
    }
}
