using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §1299 — a volunteer task belongs to a CATEGORY; the subcategory is optional and is only ever
/// named by an organizer (operator 2026-09-23: <i>"it must be possible to assign tasks to a
/// category - subcategory should be optional. and subcategories can be called anything like
/// Monday, Tuesday, Wednesday - not general as today"</i>).
/// </summary>
public sealed class VolunteerCategoryDirectTests
{
    private const int EventId = 91;
    private const int OrganizerId = 1;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-23T10:00:00Z");
    }

    private static CommunityHubDbContext NewDb()
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vol-cat-direct-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event { Id = EventId, CommunityName = "C", DisplayName = "C27", Code = "C27", IsActive = true });
        db.SaveChanges();
        return db;
    }

    private static VolunteerStructureService NewStructure(CommunityHubDbContext db) =>
        new(db, new FixedClock(), VolunteerAssignmentGate.For(db));

    private static VolunteerStructureService.ActorContext Organizer() =>
        new(OrganizerId, "org@example.test", ParticipantRole.Organizer, EventId);

    [Fact]
    public async Task A_task_can_be_created_directly_under_a_category()
    {
        using var db = NewDb();
        var svc = NewStructure(db);
        var cat = await svc.CreateCategoryAsync(Organizer(), "Check-in", null);

        var task = await svc.CreateTaskInCategoryAsync(Organizer(), cat.Id, "Desk morning", null, null, "07:00");

        Assert.Equal(cat.Id, task.CategoryId);
        Assert.Null(task.SubcategoryId);
        Assert.False(await db.VolunteerSubcategories.AnyAsync());

        var tree = Assert.Single(await svc.LoadTreeAsync(EventId));
        Assert.Single(tree.Tasks);                 // shown under the category itself
        Assert.Empty(tree.Subcategories);
    }

    [Fact]
    public async Task A_task_created_in_a_subcategory_carries_that_subcategorys_category()
    {
        using var db = NewDb();
        var svc = NewStructure(db);
        var cat = await svc.CreateCategoryAsync(Organizer(), "Check-in", null);
        var monday = await svc.CreateSubcategoryAsync(Organizer(), cat.Id, "Monday", null);

        var task = await svc.CreateTaskAsync(Organizer(), monday.Id, "Desk morning", null, null, null);

        Assert.Equal(monday.Id, task.SubcategoryId);
        Assert.Equal(cat.Id, task.CategoryId);
    }

    /// <summary>🔒 The save seam keeps CategoryId in step with the subcategory for every writer,
    /// including one that only sets the subcategory (older paths, tests, bulk edits).</summary>
    [Fact]
    public async Task Setting_only_the_subcategory_fills_in_the_category()
    {
        using var db = NewDb();
        var a = new VolunteerCategory { EventId = EventId, Name = "A" };
        var b = new VolunteerCategory { EventId = EventId, Name = "B" };
        db.VolunteerCategories.AddRange(a, b);
        await db.SaveChangesAsync();
        var bSub = new VolunteerSubcategory { EventId = EventId, CategoryId = b.Id, Name = "Tuesday" };
        db.VolunteerSubcategories.Add(bSub);
        await db.SaveChangesAsync();

        var t = new VolunteerTask { EventId = EventId, CategoryId = a.Id, Title = "Carry boxes" };
        db.VolunteerTasks.Add(t);
        await db.SaveChangesAsync();
        Assert.Equal(a.Id, t.CategoryId);

        t.SubcategoryId = bSub.Id;                  // only the subcategory changes
        await db.SaveChangesAsync();
        Assert.Equal(b.Id, t.CategoryId);           // …and the category followed it
    }

    [Fact]
    public async Task Removing_a_subcategory_moves_its_tasks_up_and_keeps_their_volunteers()
    {
        using var db = NewDb();
        var svc = NewStructure(db);
        var cat = await svc.CreateCategoryAsync(Organizer(), "Check-in", null);
        var monday = await svc.CreateSubcategoryAsync(Organizer(), cat.Id, "Monday", null);
        var task = await svc.CreateTaskAsync(Organizer(), monday.Id, "Desk morning", null, null, null);
        var vol = new Participant
        {
            EventId = EventId, Email = "v@example.test", FullName = "V", Role = ParticipantRole.Volunteer, IsActive = true,
        };
        db.Participants.Add(vol);
        await db.SaveChangesAsync();
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment { EventId = EventId, TaskId = task.Id, ParticipantId = vol.Id });
        await db.SaveChangesAsync();

        Assert.True(await svc.DeleteSubcategoryAsync(Organizer(), monday.Id));

        var after = await db.VolunteerTasks.SingleAsync(t => t.Id == task.Id);   // not deleted
        Assert.Null(after.SubcategoryId);
        Assert.Equal(cat.Id, after.CategoryId);
        Assert.True(await db.VolunteerTaskAssignments.AnyAsync(a => a.TaskId == task.Id));
        Assert.False(await db.VolunteerSubcategories.AnyAsync());
    }

    [Fact]
    public async Task Plan_import_puts_tasks_directly_in_the_bucket_and_does_not_duplicate_a_filed_one()
    {
        using var db = NewDb();
        var csv = "Time End;Task Name;Status;Criticality;Responsible Team;ELDK Lead;Resources Needed;Resource Names;Pre-req;Expectations\n"
                + "10:00;Registration desk;Open;Need to have;Front of house;Lead;1;;;\n";
        var importer = new VolunteerPlanImportService(
            db, new HeuristicTaskGuidanceGenerator(), new FixedClock(), VolunteerAssignmentGate.For(db));

        await importer.ImportAsync(EventId, new VolunteerPlanParser().Parse(csv));

        var task = await db.VolunteerTasks.SingleAsync();
        Assert.Null(task.SubcategoryId);
        Assert.False(await db.VolunteerSubcategories.AnyAsync());   // no "Imported plan"

        // The organizer files it under "Monday"; a re-import must find it there, not duplicate it
        // and not move it back out.
        var monday = await NewStructure(db).CreateSubcategoryAsync(Organizer(), task.CategoryId, "Monday", null);
        task.SubcategoryId = monday.Id;
        await db.SaveChangesAsync();

        await importer.ImportAsync(EventId, new VolunteerPlanParser().Parse(csv));

        var again = await db.VolunteerTasks.SingleAsync();
        Assert.Equal(task.Id, again.Id);
        Assert.Equal(monday.Id, again.SubcategoryId);
    }

    /// <summary>§1299 — ONE shared queue: organizer B sees, and the planner counts, what organizer A
    /// queued.</summary>
    [Fact]
    public async Task The_volunteer_queue_is_shared_between_organizers()
    {
        using var db = NewDb();
        var svc = NewStructure(db);
        var cat = await svc.CreateCategoryAsync(Organizer(), "Check-in", null);
        var task = await svc.CreateTaskInCategoryAsync(Organizer(), cat.Id, "Desk", null, null, null, resourcesNeeded: 2);
        var vol = new Participant
        {
            EventId = EventId, Email = "v@example.test", FullName = "V", Role = ParticipantRole.Volunteer, IsActive = true,
        };
        db.Participants.Add(vol);
        await db.SaveChangesAsync();

        Assert.True(await svc.AssignVolunteerAsync(Organizer(), task.Id, vol.Id));

        var orgB = new VolunteerStructureService.ActorContext(OrganizerId + 1, "b@example.test", ParticipantRole.Organizer, EventId);
        var queue = new VolunteerAllocationService(db, new FixedClock(),
            new CommunityHub.Core.Settings.FeatureGateService(db), new CommunityHub.Core.Settings.RingResolver(db));

        Assert.Single(await queue.LoadDraftAsync(orgB));
        var cov = await queue.LoadTaskCoverageAsync(orgB, task.Id);
        Assert.Equal(1, cov!.DraftCount);

        // B queuing the same person again is idempotent, not a duplicate-key failure.
        Assert.True(await queue.AddDraftAsync(orgB, task.Id, vol.Id));
        Assert.Equal(1, await db.TaskAllocationDrafts.CountAsync());

        // Removing them from the task in the structure un-queues them.
        Assert.True(await svc.UnassignVolunteerAsync(Organizer(), task.Id, vol.Id));
        Assert.False(await db.TaskAllocationDrafts.AnyAsync());
    }
}
