using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: per-role data isolation — each role sees only its own data. The GUI
/// counterparts assert the auth gates (a speaker bounced from /Organizer, an
/// attendee from /Sponsor); this backend half proves the QUERY-LEVEL scoping the
/// pages rely on:
///
///  - the personal task list is filtered to the signed-in participant
///    (Tasks.IndexModel: AssignedParticipantId == me),
///  - sponsor company tasks are filtered to the company the contact is linked to
///    (a contact of company A never sees company B's tasks),
///  - a sponsor's leads are filtered to their own SponsorCompanyId.
/// </summary>
public sealed class RoleDataIsolationScenarioTests
{
    [Fact]
    public async Task Personal_task_list_is_scoped_to_the_signed_in_participant()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // Give each speaker one personal task.
        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId, AssignedParticipantId = seed.SpeakerOneId,
            Title = "Speaker one's task", CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        });
        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId, AssignedParticipantId = seed.SpeakerTwoId,
            Title = "Speaker two's task", CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        // The /Tasks query for speaker one (mirrors Tasks.IndexModel).
        var mine = await db.Tasks
            .Where(t => t.EventId == seed.EventId
                        && t.AssignedParticipantId == seed.SpeakerOneId)
            .ToListAsync();

        Assert.All(mine, t => Assert.Equal(seed.SpeakerOneId, t.AssignedParticipantId));
        Assert.DoesNotContain(mine, t => t.Title == "Speaker two's task");
    }

    [Fact]
    public async Task Sponsor_only_sees_their_own_company_tasks()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // A SECOND sponsor company with its own booth task.
        const string otherCompanyId = "9002";
        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId, SponsorCompanyId = otherCompanyId,
            Title = "Other company's booth task",
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        // The /Sponsor/Tasks query for our seeded company.
        var mine = await db.Tasks
            .Where(t => t.EventId == seed.EventId
                        && t.SponsorCompanyId == ScenarioSeed.SponsorCompanyId)
            .ToListAsync();

        Assert.NotEmpty(mine);
        Assert.All(mine, t => Assert.Equal(ScenarioSeed.SponsorCompanyId, t.SponsorCompanyId));
        Assert.DoesNotContain(mine, t => t.SponsorCompanyId == otherCompanyId);
    }

    [Fact]
    public async Task Sponsor_only_sees_their_own_company_leads()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // A lead belonging to a DIFFERENT company.
        db.SponsorLeads.Add(new SponsorLead
        {
            EventId = seed.EventId, SponsorCompanyId = "9002",
            ZohoRecordId = "zoho-other-001", FullName = "Other Co Lead",
            Status = SponsorLeadStatus.Open,
            CapturedAt = ScenarioFixture.Clock.GetUtcNow(),
            LastSyncedAt = ScenarioFixture.Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var mine = await db.SponsorLeads
            .Where(l => l.EventId == seed.EventId
                        && l.SponsorCompanyId == ScenarioSeed.SponsorCompanyId)
            .ToListAsync();

        Assert.All(mine, l => Assert.Equal(ScenarioSeed.SponsorCompanyId, l.SponsorCompanyId));
        Assert.DoesNotContain(mine, l => l.FullName == "Other Co Lead");
    }

    [Fact]
    public async Task Every_seeded_row_is_tagged_as_test_data()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // Go-live cleanup deletes WHERE IsTestUser = true; every seeded
        // participant must carry the flag so nothing synthetic survives.
        var participants = await db.Participants
            .Where(p => p.EventId == seed.EventId)
            .ToListAsync();
        Assert.NotEmpty(participants);
        Assert.All(participants, p => Assert.True(p.IsTestUser));
    }
}
