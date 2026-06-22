using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Actor = CommunityHub.Core.Domain.VolunteerStructureService.ActorContext;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: the volunteer "Buckets" feature on top of the existing structure —
/// bucket → supervisor(s) → ELDK lead, the plan CSV importer, the gated AI guidance
/// seam, mark-complete-by-lead, red/green gap detection, the draft→commit
/// allocation queue, and the volunteer view.
///
/// All CSV fixtures use FAKE person names only — the real plan file is never copied
/// into the repo.
/// </summary>
public sealed class VolunteerBucketsScenarioTests
{
    private static VolunteerStructureService NewStructure(CommunityHub.Core.Data.CommunityHubDbContext db)
        => new(db, ScenarioFixture.Clock);

    private static VolunteerAllocationService NewAllocation(CommunityHub.Core.Data.CommunityHubDbContext db)
        => new(db, ScenarioFixture.Clock,
               new CommunityHub.Core.Settings.FeatureGateService(db),
               new CommunityHub.Core.Settings.RingResolver(db));

    private static Actor Organizer(ScenarioSeed.SeedResult s)
        => new(s.OrganizerId, ScenarioSeed.OrganizerEmail, ParticipantRole.Organizer, s.EventId);

    private static Actor VolunteerActor(ScenarioSeed.SeedResult s, int id, string email)
        => new(id, email, ParticipantRole.Volunteer, s.EventId);

    private static async Task<int> AddVolunteerAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, ScenarioSeed.SeedResult s, string name, string email)
    {
        var p = new Participant
        {
            EventId = s.EventId, Email = email, FullName = name,
            Role = ParticipantRole.Volunteer, IsActive = true, IsTestUser = true,
            CreatedAt = ScenarioFixture.Clock.GetUtcNow(),
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    // =====================================================================
    //  1. Bucket → supervisor(s) → ELDK lead model
    // =====================================================================

    [Fact]
    public async Task Bucket_supports_multiple_supervisors_and_an_eldk_lead()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewStructure(db);
        var org = Organizer(seed);

        var supA = await AddVolunteerAsync(db, seed, "Sam Supervisor", "sam.sup@example.test");
        var supB = await AddVolunteerAsync(db, seed, "Robin Supervisor", "robin.sup@example.test");

        var bucket = await svc.CreateCategoryAsync(org, "A/V", "Audio-visual");

        // Two supervisors (multi-supervisor model) + the ELDK lead (third tier).
        Assert.True(await svc.AddSupervisorAsync(org, bucket.Id, supA));
        Assert.True(await svc.AddSupervisorAsync(org, bucket.Id, supB));
        Assert.True(await svc.SetBucketEldkLeadAsync(org, bucket.Id, "Alex Lead"));

        var supervisors = await svc.LoadSupervisorsAsync(seed.EventId, bucket.Id);
        Assert.Equal(2, supervisors.Count);
        Assert.Contains(supervisors, p => p.Id == supA);
        Assert.Contains(supervisors, p => p.Id == supB);

        var reloaded = await db.VolunteerCategories.FirstAsync(c => c.Id == bucket.Id);
        Assert.Equal("Alex Lead", reloaded.EldkLeadName);
    }

    [Fact]
    public async Task Both_supervisor_tiers_grant_management_rights()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewStructure(db);
        var org = Organizer(seed);

        var supId = await AddVolunteerAsync(db, seed, "Sam Supervisor", "sam2.sup@example.test");
        var bucket = await svc.CreateCategoryAsync(org, "Photo", null);
        await svc.AddSupervisorAsync(org, bucket.Id, supId);

        var supActor = VolunteerActor(seed, supId, "sam2.sup@example.test");
        Assert.True(await svc.CanManageCategoryAsync(supActor, bucket.Id));

        // A plain volunteer (the seed volunteer) cannot manage the bucket.
        var plain = VolunteerActor(seed, seed.VolunteerId, ScenarioSeed.VolunteerEmail);
        Assert.False(await svc.CanManageCategoryAsync(plain, bucket.Id));
    }

    // =====================================================================
    //  2. CSV importer → buckets + tasks (FAKE-name fixture)
    // =====================================================================

    // FAKE names only — the real plan file is never copied into the repo.
    private const string FakePlanCsv =
        "T-day;Date;Time Start;Time End;Task Name;Status;Criticality;Responsible Team;ELDK Lead Task;Resources Needed;Resource Names;Pre-req;Expectations\n" +
        "T-1;20-02-2026;09:00;12:00;Print badges for attendees;Completed;Need-to-have;BadgeTeam;Alex Lead;2;\"Jordan Helper\nCasey Helper\";;All badges printed\n" +
        "T-1;20-02-2026;09:00;12:00;Setup A/V in main hall;;Need-to-have;AvTeam;Robin Lead;3;\"Jordan Helper\";Cables on site;\n" +
        "\n" +
        "T-0;23-02-2026;08:00;10:00;Pack attendee bags;;Nice-to-have;BadgeTeam;Alex Lead;1;Unknown Person;;Bags ready\n";

    [Fact]
    public void Parser_derives_buckets_from_responsible_team_and_parses_fields()
    {
        var parser = new VolunteerPlanParser();
        var plan = parser.Parse(FakePlanCsv);

        // 3 task rows (blank separator skipped), 2 distinct buckets.
        Assert.Equal(3, plan.Tasks.Count);
        Assert.Equal(new[] { "BadgeTeam", "AvTeam" }, plan.Buckets);

        var badge = plan.Tasks.First(t => t.Title == "Print badges for attendees");
        Assert.Equal("BadgeTeam", badge.BucketName);
        Assert.Equal(VolunteerTaskStatus.Done, badge.Status);
        Assert.Equal(VolunteerTaskCriticality.NeedToHave, badge.Criticality);
        Assert.Equal("Alex Lead", badge.EldkLeadName);
        Assert.Equal(2, badge.ResourcesNeeded);
        Assert.Equal(new[] { "Jordan Helper", "Casey Helper" }, badge.ResourceNames);
        Assert.Equal("All badges printed", badge.Expectations);

        var av = plan.Tasks.First(t => t.Title == "Setup A/V in main hall");
        Assert.Equal("Cables on site", av.Prerequisites);
        Assert.Equal(3, av.ResourcesNeeded);
    }

    [Fact]
    public async Task Import_creates_buckets_tasks_and_links_matched_names()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        // Volunteers whose FullName matches names in the fixture.
        var jordan = await AddVolunteerAsync(db, seed, "Jordan Helper", "jordan@example.test");
        var casey = await AddVolunteerAsync(db, seed, "Casey Helper", "casey@example.test");

        var parser = new VolunteerPlanParser();
        var plan = parser.Parse(FakePlanCsv);
        var importer = new VolunteerPlanImportService(db, new HeuristicTaskGuidanceGenerator(), ScenarioFixture.Clock);

        var result = await importer.ImportAsync(seed.EventId, plan);

        Assert.Equal(2, result.BucketsCreated);
        Assert.Equal(3, result.TasksCreated);
        // Jordan (x2 tasks) + Casey (x1) = 3 real assignments; "Unknown Person" unmatched.
        Assert.Equal(3, result.AssignmentsLinked);
        Assert.Equal(1, result.NamesUnmatched);
        Assert.Contains("Unknown Person", result.UnmatchedNames);

        // Buckets are real categories.
        var buckets = await db.VolunteerCategories.Where(c => c.EventId == seed.EventId).ToListAsync();
        Assert.Contains(buckets, b => b.Name == "BadgeTeam");
        Assert.Contains(buckets, b => b.Name == "AvTeam");

        // Guidance was filled for tasks missing pre-req/expectations (heuristic).
        var avTask = await db.VolunteerTasks.FirstAsync(t => t.Title == "Setup A/V in main hall");
        Assert.False(string.IsNullOrWhiteSpace(avTask.Expectations)); // was blank in CSV, filled
        Assert.Equal("Cables on site", avTask.Prerequisites);          // kept from CSV
    }

    [Fact]
    public async Task Import_is_idempotent()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var parser = new VolunteerPlanParser();
        var plan = parser.Parse(FakePlanCsv);
        var importer = new VolunteerPlanImportService(db, new HeuristicTaskGuidanceGenerator(), ScenarioFixture.Clock);

        await importer.ImportAsync(seed.EventId, plan);
        var second = await importer.ImportAsync(seed.EventId, plan);

        Assert.Equal(0, second.BucketsCreated);
        Assert.Equal(0, second.TasksCreated);
        Assert.Equal(2, await db.VolunteerCategories.CountAsync(c => c.EventId == seed.EventId));
        Assert.Equal(3, await db.VolunteerTasks.CountAsync(t => t.EventId == seed.EventId));
    }

    // =====================================================================
    //  3. AI seam — gated + heuristic + editable
    // =====================================================================

    [Fact]
    public async Task Heuristic_generator_is_not_ai_backed_and_produces_guidance()
    {
        var gen = new HeuristicTaskGuidanceGenerator();
        Assert.False(gen.IsAiBacked);

        var g = await gen.GenerateAsync("Print badges for volunteers", "BadgeTeam", "BadgeTeam");
        Assert.False(string.IsNullOrWhiteSpace(g.Prerequisites));
        Assert.False(string.IsNullOrWhiteSpace(g.Expectations));
    }

    [Fact]
    public void Llm_generator_is_gated_off_without_a_key()
    {
        var opts = new TaskGuidanceOptions(); // no ApiKey
        Assert.False(opts.IsConfigured);

        var llm = new LlmTaskGuidanceGenerator(
            new HttpClient(), opts, new HeuristicTaskGuidanceGenerator());
        Assert.False(llm.IsAiBacked); // gate is closed
    }

    [Fact]
    public async Task Llm_generator_falls_back_to_heuristic_when_disabled()
    {
        var opts = new TaskGuidanceOptions(); // disabled
        // HttpClient with no base address: if it tried to call out it would throw,
        // proving the gate prevents any network call.
        var llm = new LlmTaskGuidanceGenerator(
            new HttpClient(), opts, new HeuristicTaskGuidanceGenerator());

        var g = await llm.GenerateAsync("Setup A/V in main hall");
        Assert.False(string.IsNullOrWhiteSpace(g.Expectations)); // came from heuristic, no throw
    }

    // =====================================================================
    //  4. Mark complete by lead
    // =====================================================================

    [Fact]
    public async Task Eldk_lead_can_mark_task_completed_with_audit_stamp()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewStructure(db);
        var org = Organizer(seed);

        var bucket = await svc.CreateCategoryAsync(org, "BadgeTeam", null);
        var sub = await svc.CreateSubcategoryAsync(org, bucket.Id, "Desk", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Print badges", null, null, null);

        Assert.True(await svc.MarkTaskCompletedByLeadAsync(org, task.Id));

        var done = await db.VolunteerTasks.FirstAsync(t => t.Id == task.Id);
        Assert.Equal(VolunteerTaskStatus.Done, done.Status);
        Assert.NotNull(done.CompletedAt);
        Assert.Equal(ScenarioSeed.OrganizerEmail, done.CompletedByEmail);

        // Reopen clears the stamp.
        Assert.True(await svc.ReopenTaskAsync(org, task.Id));
        var reopened = await db.VolunteerTasks.FirstAsync(t => t.Id == task.Id);
        Assert.Equal(VolunteerTaskStatus.Open, reopened.Status);
        Assert.Null(reopened.CompletedAt);
    }

    [Fact]
    public async Task Plain_volunteer_cannot_mark_complete_by_lead()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewStructure(db);
        var org = Organizer(seed);

        var bucket = await svc.CreateCategoryAsync(org, "BadgeTeam", null);
        var sub = await svc.CreateSubcategoryAsync(org, bucket.Id, "Desk", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Print badges", null, null, null);

        var plain = VolunteerActor(seed, seed.VolunteerId, ScenarioSeed.VolunteerEmail);
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => svc.MarkTaskCompletedByLeadAsync(plain, task.Id));
    }

    // =====================================================================
    //  5. Gap detection (needed vs assigned) — red/green
    // =====================================================================

    [Fact]
    public async Task Gap_is_red_when_short_and_green_when_covered()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewStructure(db);
        var alloc = NewAllocation(db);
        var org = Organizer(seed);

        var bucket = await svc.CreateCategoryAsync(org, "BadgeTeam", null);
        var sub = await svc.CreateSubcategoryAsync(org, bucket.Id, "Desk", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Print badges", null, null, null,
            resourcesNeeded: 2);

        var v1 = await AddVolunteerAsync(db, seed, "Vol One", "v1@example.test");

        // Needed 2, assigned 0 -> RED.
        var cov0 = await alloc.LoadTaskCoverageAsync(org, task.Id);
        Assert.NotNull(cov0);
        Assert.False(cov0!.IsCovered);
        Assert.Equal(2, cov0.SimulatedShortfall);

        // Assign one really -> still RED (1/2).
        await svc.AssignVolunteerAsync(org, task.Id, v1);
        var v2 = await AddVolunteerAsync(db, seed, "Vol Two", "v2@example.test");
        await svc.AssignVolunteerAsync(org, task.Id, v2);

        // Now 2/2 -> GREEN.
        var cov2 = await alloc.LoadTaskCoverageAsync(org, task.Id);
        Assert.True(cov2!.IsCovered);
        Assert.Equal(0, cov2.SimulatedShortfall);
    }

    // =====================================================================
    //  6. Draft allocation: simulation vs commit vs discard (THE key piece)
    // =====================================================================

    [Fact]
    public async Task Draft_simulates_coverage_without_assigning_until_commit()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewStructure(db);
        var alloc = NewAllocation(db);
        var org = Organizer(seed);

        var bucket = await svc.CreateCategoryAsync(org, "BadgeTeam", null);
        var sub = await svc.CreateSubcategoryAsync(org, bucket.Id, "Desk", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Print badges", null, null, null,
            resourcesNeeded: 1);

        var v1 = await AddVolunteerAsync(db, seed, "Vol One", "dv1@example.test");

        // Queue the volunteer into the DRAFT — simulation only.
        Assert.True(await alloc.AddDraftAsync(org, task.Id, v1));

        // Simulation shows GREEN (1 needed, 0 assigned + 1 draft)...
        var cov = await alloc.LoadTaskCoverageAsync(org, task.Id);
        Assert.True(cov!.IsCovered);
        Assert.Equal(1, cov.DraftCount);
        Assert.Equal(0, cov.AssignedCount);

        // ...but NOTHING is actually assigned yet.
        Assert.Equal(0, await db.VolunteerTaskAssignments.CountAsync(a => a.TaskId == task.Id));

        // COMMIT turns the draft into a real assignment and clears the queue.
        var commit = await alloc.CommitAsync(org);
        Assert.Equal(1, commit.Committed);
        Assert.Equal(1, await db.VolunteerTaskAssignments.CountAsync(a => a.TaskId == task.Id));
        Assert.Equal(0, await db.TaskAllocationDrafts.CountAsync(d => d.EventId == seed.EventId));
    }

    [Fact]
    public async Task Discard_resets_the_draft_without_assigning()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewStructure(db);
        var alloc = NewAllocation(db);
        var org = Organizer(seed);

        var bucket = await svc.CreateCategoryAsync(org, "BadgeTeam", null);
        var sub = await svc.CreateSubcategoryAsync(org, bucket.Id, "Desk", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Print badges", null, null, null,
            resourcesNeeded: 1);
        var v1 = await AddVolunteerAsync(db, seed, "Vol One", "rv1@example.test");

        await alloc.AddDraftAsync(org, task.Id, v1);
        var discarded = await alloc.DiscardAsync(org);

        Assert.Equal(1, discarded);
        Assert.Equal(0, await db.TaskAllocationDrafts.CountAsync(d => d.EventId == seed.EventId));
        Assert.Equal(0, await db.VolunteerTaskAssignments.CountAsync(a => a.TaskId == task.Id));
    }

    [Fact]
    public async Task Volunteer_cannot_allocate()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewStructure(db);
        var alloc = NewAllocation(db);
        var org = Organizer(seed);

        var bucket = await svc.CreateCategoryAsync(org, "BadgeTeam", null);
        var sub = await svc.CreateSubcategoryAsync(org, bucket.Id, "Desk", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Print badges", null, null, null, resourcesNeeded: 1);

        var plain = VolunteerActor(seed, seed.VolunteerId, ScenarioSeed.VolunteerEmail);
        await Assert.ThrowsAsync<VolunteerAccessDeniedException>(
            () => alloc.AddDraftAsync(plain, task.Id, seed.VolunteerId));
    }

    // =====================================================================
    //  7. Volunteer view: my tasks + bucket supervisor(s) + ELDK lead
    // =====================================================================

    [Fact]
    public async Task Volunteer_view_shows_assigned_task_with_instructions_and_supervisors()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = NewStructure(db);
        var org = Organizer(seed);

        var supId = await AddVolunteerAsync(db, seed, "Sam Supervisor", "vv.sup@example.test");
        var bucket = await svc.CreateCategoryAsync(org, "BadgeTeam", null);
        await svc.AddSupervisorAsync(org, bucket.Id, supId);
        await svc.SetBucketEldkLeadAsync(org, bucket.Id, "Alex Lead");

        var sub = await svc.CreateSubcategoryAsync(org, bucket.Id, "Desk", null);
        var task = await svc.CreateTaskAsync(org, sub.Id, "Print badges", null, null, null,
            instructions: "Use the desk printer; stack by role.");
        await svc.AssignVolunteerAsync(org, task.Id, seed.VolunteerId);

        // Volunteer's "my tasks" view.
        var myTasks = await svc.LoadMyTasksAsync(seed.EventId, seed.VolunteerId);
        var mine = Assert.Single(myTasks);
        Assert.Equal("Print badges", mine.Title);
        Assert.Equal("Use the desk printer; stack by role.", mine.Instructions);

        // The volunteer can see their bucket's supervisor(s) + the ELDK lead.
        var supervisors = await svc.LoadSupervisorsAsync(seed.EventId, mine.Subcategory.CategoryId);
        Assert.Contains(supervisors, p => p.Id == supId);
        var bucketRow = await db.VolunteerCategories.FirstAsync(c => c.Id == mine.Subcategory.CategoryId);
        Assert.Equal("Alex Lead", bucketRow.EldkLeadName);
    }
}
