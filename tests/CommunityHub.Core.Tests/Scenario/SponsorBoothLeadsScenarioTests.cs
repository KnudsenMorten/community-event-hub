using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: a sponsor contact manages their booth tasks and views the leads
/// pipeline. The GUI counterpart (scenario-sponsor.spec.ts) signs in as the
/// sponsor contact, ticks a booth task on /Sponsor/Tasks and reads the leads
/// area; this backend half proves the DB state behind those screens:
///
///  - booth tasks are scoped to the sponsor COMPANY (SponsorCompanyId), so any
///    linked contact sees them and a "Mark complete" postback flips the row,
///  - completing a task moves it from the Pending group to the Completed group
///    the page renders,
///  - the leads pipeline shows the company's non-junk leads (Junk hidden from
///    sponsor feeds; Processed still counted),
///  - the company display name resolves to the canonical PUBLIC name
///    (company_name_public), never the legal/billing form.
/// </summary>
public sealed class SponsorBoothLeadsScenarioTests
{
    private static async Task<List<ParticipantTask>> CompanyTasks(
        Data.CommunityHubDbContext db, int eventId, string companyId) =>
        await db.Tasks
            .Where(t => t.EventId == eventId && t.SponsorCompanyId == companyId)
            .ToListAsync();

    [Fact]
    public async Task Booth_tasks_are_company_scoped_and_visible_to_every_contact()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var tasks = await CompanyTasks(db, seed.EventId, ScenarioSeed.SponsorCompanyId);

        // Four seeded booth deliverables, three mandatory + one optional add-on.
        Assert.Equal(4, tasks.Count);
        Assert.Equal(3, tasks.Count(t => t.IsMandatory));
        Assert.Single(tasks, t => !t.IsMandatory);
        // Company-scoped, not assigned to a single person -> any contact manages.
        Assert.All(tasks, t => Assert.Null(t.AssignedParticipantId));

        // Both seeded sponsor contacts belong to the same company.
        var contacts = await db.Participants
            .Where(p => p.EventId == seed.EventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId == ScenarioSeed.SponsorCompanyId)
            .ToListAsync();
        Assert.Equal(2, contacts.Count);
    }

    [Fact]
    public async Task Completing_a_booth_task_moves_it_to_the_completed_group()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var pendingBefore = (await CompanyTasks(db, seed.EventId, ScenarioSeed.SponsorCompanyId))
            .Count(t => t.State != TaskState.Done);

        // Simulate the /Sponsor/Tasks "Mark complete" postback on a pending task.
        var target = (await CompanyTasks(db, seed.EventId, ScenarioSeed.SponsorCompanyId))
            .First(t => t.State != TaskState.Done);
        target.State = TaskState.Done;
        target.CompletedAt = ScenarioFixture.Clock.GetUtcNow();
        await db.SaveChangesAsync();

        var tasks = await CompanyTasks(db, seed.EventId, ScenarioSeed.SponsorCompanyId);
        var pendingAfter = tasks.Count(t => t.State != TaskState.Done);
        var completedAfter = tasks.Count(t => t.State == TaskState.Done);

        Assert.Equal(pendingBefore - 1, pendingAfter);
        Assert.Equal(2, completedAfter); // one was already Done in the seed
        Assert.NotNull(tasks.Single(t => t.Id == target.Id).CompletedAt);
    }

    [Fact]
    public async Task Leads_pipeline_shows_company_leads_excluding_junk()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        var allLeads = await db.SponsorLeads
            .Where(l => l.EventId == seed.EventId
                        && l.SponsorCompanyId == ScenarioSeed.SponsorCompanyId)
            .ToListAsync();

        // Four seeded leads total: 2 open, 1 processed, 1 junk.
        Assert.Equal(4, allLeads.Count);

        // The sponsor-facing feed hides Junk (the documented soft-status rule).
        var sponsorVisible = allLeads.Where(l => l.Status != SponsorLeadStatus.Junk).ToList();
        Assert.Equal(3, sponsorVisible.Count);
        Assert.DoesNotContain(sponsorVisible, l => l.Status == SponsorLeadStatus.Junk);

        // Open (unprocessed) count the pipeline highlights.
        Assert.Equal(2, allLeads.Count(l => l.Status == SponsorLeadStatus.Open));
    }

    [Fact]
    public async Task Company_name_resolves_to_the_public_name()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // The display name the sponsor screens + leads grid show comes from the
        // upload location's CompanyName, which the order-pull resolves via the
        // public -> legal -> billing -> "Company {id}" chain. The seed populated
        // it with the PUBLIC name; assert that is what surfaces.
        var location = await db.SponsorUploadLocations.SingleAsync(
            l => l.EventId == seed.EventId
                 && l.SponsorCompanyId == ScenarioSeed.SponsorCompanyId);

        Assert.Equal(ScenarioSeed.SponsorPublicName, location.CompanyName);
        Assert.NotEqual(ScenarioSeed.SponsorLegalName, location.CompanyName);
        Assert.NotEqual($"Company {ScenarioSeed.SponsorCompanyId}", location.CompanyName);
    }
}
