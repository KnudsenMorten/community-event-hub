using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §1295 — volunteer task assignment is RING-GATED on every write path (operator 2026-09-21:
/// <i>"i want to start onboarding and activation of volunteers. but i dont want volunteer task
/// assignment to happen. we just put ring gate on that functionality if not made"</i>).
/// </summary>
/// <remarks>
/// <para>🔴 <b>The point of this suite is the FIVE paths, not the gate arithmetic.</b> A gate
/// existed before and still let three paths through — the supervisor dashboard, the allocation
/// scenario commit and the CSV plan import — because it lived in one page instead of in the
/// services. Each path therefore gets its own case here; a new write path that skips
/// <see cref="VolunteerAssignmentGate"/> should fail something.</para>
///
/// <para>The default state under test is the SHIPPED default: the feature OFF at Ring 1, which is
/// the "volunteers onboard, nobody gets work" state he asked for. Released cases write the same
/// FeatureSetting row the Settings page writes.</para>
/// </remarks>
public sealed class VolunteerAssignmentGateTests
{
    private const int EventId = 77;
    private const int OrganizerId = 1;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-21T10:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vol-assign-gate-{Guid.NewGuid():N}").Options);

    private static VolunteerStructureService NewStructure(CommunityHubDbContext db) =>
        new(db, new FixedClock(), VolunteerAssignmentGate.For(db));

    private static VolunteerStructureService.ActorContext Organizer() =>
        new(OrganizerId, "org@example.test", ParticipantRole.Organizer, EventId);

    private static void SeedEvent(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true,
        });
        db.SaveChanges();
    }

    /// <summary>Release the feature to <paramref name="ring"/> — what the Settings page writes.</summary>
    private static void Release(CommunityHubDbContext db, Ring ring)
    {
        db.FeatureSettings.Add(new FeatureSetting
        {
            EventId = EventId,
            FeatureKey = VolunteerAssignmentGate.FeatureKey,
            Enabled = true,
            ReleasedToRingOverride = ring,
        });
        db.SaveChanges();
    }

    private static int AddVolunteer(CommunityHubDbContext db, string email, Ring ring = Ring.Broad)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = email.Split('@')[0],
            Role = ParticipantRole.Volunteer, IsActive = true, Ring = ring,
        };
        db.Participants.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    /// <summary>A bucket to subcategory to task, returning the task id.</summary>
    private static int AddTask(CommunityHubDbContext db, string title = "Registration desk")
    {
        var cat = new VolunteerCategory { EventId = EventId, Name = "Front of house" };
        db.VolunteerCategories.Add(cat);
        db.SaveChanges();
        var sub = new VolunteerSubcategory { EventId = EventId, CategoryId = cat.Id, Name = "Desk" };
        db.VolunteerSubcategories.Add(sub);
        db.SaveChanges();
        var t = new VolunteerTask
        {
            EventId = EventId, SubcategoryId = sub.Id, Title = title, ResourcesNeeded = 3,
        };
        db.VolunteerTasks.Add(t);
        db.SaveChanges();
        return t.Id;
    }

    // ---------------------------------------------------------------------
    //  The default state: onboarding runs, assignment does not.
    // ---------------------------------------------------------------------

    // §1299 — the work structure's Assign QUEUES; PUBLISH (the queue's commit) is where the ring
    // gate applies. Operator 2026-09-23: "the work items must be internal until we publish them"
    // · "the publishing should be ringgated" · "so we can publish tasks to fx ring 2 and then
    // ring 3 volunteers will not see their tasks".

    private static VolunteerAllocationService NewQueue(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));

    /// <summary>Widen (or narrow) the already-released feature — the Settings page edits the row.</summary>
    private static void SetRing(CommunityHubDbContext db, Ring ring)
    {
        var row = db.FeatureSettings.Single(f => f.EventId == EventId
                                                 && f.FeatureKey == VolunteerAssignmentGate.FeatureKey);
        row.ReleasedToRingOverride = ring;
        db.SaveChanges();
    }

    [Fact]
    public async Task Default_state_queues_the_assignment_but_publishes_nothing()
    {
        using var db = NewDb();
        SeedEvent(db);
        var vol = AddVolunteer(db, "vol@example.test");
        var task = AddTask(db);

        // Queueing is internal, so it is allowed with the switch off — it is how work is staged.
        Assert.True(await NewStructure(db).AssignVolunteerAsync(Organizer(), task, vol));
        Assert.False(await db.VolunteerTaskAssignments.AnyAsync());
        Assert.Equal(1, await db.TaskAllocationDrafts.CountAsync());

        // Publishing is refused per target and the draft is KEPT, not lost.
        var r = await NewQueue(db).CommitAsync(Organizer());
        Assert.Equal(0, r.Committed);
        Assert.Equal(1, r.SkippedOutOfRing);
        Assert.False(await db.VolunteerTaskAssignments.AnyAsync());
        Assert.Equal(1, await db.TaskAllocationDrafts.CountAsync());
    }

    [Fact]
    public async Task Assign_is_invisible_to_the_volunteer_until_published()
    {
        using var db = NewDb();
        SeedEvent(db);
        Release(db, Ring.Broad);
        var vol = AddVolunteer(db, "vol@example.test");
        var task = AddTask(db);
        var svc = NewStructure(db);

        Assert.True(await svc.AssignVolunteerAsync(Organizer(), task, vol));
        // Queued: every volunteer surface reads assignments only, so "My tasks" is empty.
        Assert.Empty(await svc.LoadMyTasksAsync(EventId, vol));

        var r = await NewQueue(db).CommitAsync(Organizer());
        Assert.Equal(1, r.Committed);
        Assert.Equal(new[] { vol }, r.AffectedParticipantIds);   // the one batched mail
        Assert.Single(await svc.LoadMyTasksAsync(EventId, vol));
        Assert.False(await db.TaskAllocationDrafts.AnyAsync());
    }

    /// <summary>
    /// 🔑 The operator's own example: publish to ring 2 — the ring-3 volunteer's task stays queued
    /// and unseen; widen the ring and publish again, and it lands.
    /// </summary>
    [Fact]
    public async Task Publish_to_ring2_holds_ring3_volunteers_until_the_ring_is_widened()
    {
        using var db = NewDb();
        SeedEvent(db);
        Release(db, Ring.Ring2);
        var early = AddVolunteer(db, "early@example.test", Ring.Ring2);
        var late = AddVolunteer(db, "late@example.test", Ring.Broad);   // ring 3
        var task = AddTask(db);
        var svc = NewStructure(db);

        Assert.True(await svc.AssignVolunteerAsync(Organizer(), task, early));
        Assert.True(await svc.AssignVolunteerAsync(Organizer(), task, late));

        var first = await NewQueue(db).CommitAsync(Organizer());
        Assert.Equal(1, first.Committed);
        Assert.Equal(1, first.SkippedOutOfRing);
        Assert.Single(await svc.LoadMyTasksAsync(EventId, early));
        Assert.Empty(await svc.LoadMyTasksAsync(EventId, late));            // not published to them
        Assert.True(await db.TaskAllocationDrafts.AnyAsync(d => d.ParticipantId == late));

        SetRing(db, Ring.Broad);
        var second = await NewQueue(db).CommitAsync(Organizer());
        Assert.Equal(1, second.Committed);
        Assert.Equal(new[] { late }, second.AffectedParticipantIds);        // only the newly published
        Assert.Single(await svc.LoadMyTasksAsync(EventId, late));
    }

    /// <summary>
    /// A bucket SUPERVISOR assigning from their own dashboard goes into the same shared queue —
    /// visible to every organizer, published only through the same gated commit.
    /// </summary>
    [Fact]
    public async Task Supervisor_assignment_goes_to_the_shared_queue_not_to_the_volunteer()
    {
        using var db = NewDb();
        SeedEvent(db);
        Release(db, Ring.Broad);
        var supervisor = AddVolunteer(db, "sup@example.test");
        var vol = AddVolunteer(db, "vol@example.test");
        var task = AddTask(db);

        // Make the supervisor run that bucket (the appointment row IS the elevation).
        var cat = await db.VolunteerCategories.FirstAsync();
        cat.SupervisorParticipantId = supervisor;
        await db.SaveChangesAsync();

        var actor = new VolunteerStructureService.ActorContext(
            supervisor, "sup@example.test", ParticipantRole.Volunteer, EventId);

        Assert.True(await NewStructure(db).AssignVolunteerAsync(actor, task, vol));
        Assert.False(await db.VolunteerTaskAssignments.AnyAsync());

        // A DIFFERENT organizer sees it in the shared queue and can publish it.
        var otherOrganizer = new VolunteerStructureService.ActorContext(
            OrganizerId + 500, "org2@example.test", ParticipantRole.Organizer, EventId);
        var queue = NewQueue(db);
        var draft = Assert.Single(await queue.LoadDraftAsync(otherOrganizer));
        Assert.Equal(supervisor, draft.OwnerParticipantId);                 // "queued by"
        Assert.Equal(1, (await queue.CommitAsync(otherOrganizer)).Committed);
    }

    /// <summary>
    /// The CSV plan import still imports the PLAN — buckets and tasks — and holds only the
    /// assignments, reporting the names it held so the import screen can say so.
    /// </summary>
    [Fact]
    public async Task Plan_import_creates_tasks_but_holds_assignments_and_reports_them()
    {
        using var db = NewDb();
        SeedEvent(db);
        var volId = AddVolunteer(db, "alex@example.test");
        var vol = await db.Participants.FirstAsync(p => p.Id == volId);
        vol.FullName = "Alex Example";
        await db.SaveChangesAsync();

        var csv = "Time End;Task Name;Status;Criticality;Responsible Team;ELDK Lead;Resources Needed;Resource Names;Pre-req;Expectations\n"
                + "10:00;Registration desk;Open;Need to have;Front of house;Lead;1;Alex Example;;\n";
        var plan = new VolunteerPlanParser().Parse(csv);

        var importer = new VolunteerPlanImportService(
            db, new HeuristicTaskGuidanceGenerator(), new FixedClock(), VolunteerAssignmentGate.For(db));
        var result = await importer.ImportAsync(EventId, plan);

        Assert.True(result.TasksCreated > 0);        // the plan still lands
        Assert.Equal(0, result.AssignmentsLinked);   // but nobody is put on it
        Assert.Equal(1, result.AssignmentsHeldOutOfRing);
        Assert.Contains("Alex Example", result.HeldNames!);
        Assert.Equal(0, result.NamesUnmatched);      // it MATCHED — it was held, not missed
        Assert.False(await db.VolunteerTaskAssignments.AnyAsync());
    }

    /// <summary>
    /// An allocation SCENARIO refuses the whole commit rather than dropping the held moves: a
    /// committed scenario is stamped and has no queue left to re-commit from, so a silent skip
    /// would lose them.
    /// </summary>
    [Fact]
    public async Task Allocation_scenario_commit_is_refused_while_assignment_is_held()
    {
        using var db = NewDb();
        SeedEvent(db);
        var vol = AddVolunteer(db, "vol@example.test");
        var task = AddTask(db);

        var svc = new AllocationScenarioService(db, new FixedClock(), VolunteerAssignmentGate.For(db));
        var id = await svc.CreateAsync(Organizer(), AllocationScenarioKind.VolunteerAllocation, "Reshuffle");
        await svc.AddTaskMoveAsync(Organizer(), id, vol, task);

        var ex = await Assert.ThrowsAsync<VolunteerValidationException>(
            () => svc.CommitAsync(Organizer(), id));
        Assert.Contains("Settings", ex.Message);
        Assert.False(await db.VolunteerTaskAssignments.AnyAsync());
    }

    /// <summary>
    /// The allocation QUEUE keeps the draft instead (dormant) — its documented behaviour for an
    /// out-of-ring target — so promoting the ring and re-committing picks the same draft up.
    /// </summary>
    [Fact]
    public async Task Allocation_queue_commit_holds_the_draft_while_assignment_is_held()
    {
        using var db = NewDb();
        SeedEvent(db);
        var vol = AddVolunteer(db, "vol@example.test");
        var task = AddTask(db);
        db.TaskAllocationDrafts.Add(new TaskAllocationDraft
        {
            EventId = EventId, OwnerParticipantId = OrganizerId, TaskId = task, ParticipantId = vol,
        });
        await db.SaveChangesAsync();

        var svc = new VolunteerAllocationService(
            db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));

        var held = await svc.CommitAsync(Organizer());
        Assert.Equal(0, held.Committed);
        Assert.Equal(1, held.SkippedOutOfRing);
        Assert.True(await db.TaskAllocationDrafts.AnyAsync());   // still queued, not lost

        // Release it, re-commit: the same draft now lands.
        Release(db, Ring.Broad);
        var done = await svc.CommitAsync(Organizer());
        Assert.Equal(1, done.Committed);
        Assert.Equal(1, await db.VolunteerTaskAssignments.CountAsync());
    }
}
