using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="VolunteerTaskBulkOperationService"/> — the
/// organizer multi-select bulk actions over volunteer work-structure TASKS
/// (change-status / delete-safely). Uses the EF Core InMemory provider so the
/// real DbContext mapping + queries run, no SQL. Asserts the same invariants the
/// participant + session bulk services hold: event-scoping, idempotency, an
/// accurate change-count, and linked-data-safe delete (a task with help-request
/// history is never silently destroyed).
/// </summary>
public sealed class VolunteerTaskBulkOperationServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"voltask-bulk-{Guid.NewGuid():N}")
            .Options);

    // A minimal Category → Subcategory the tasks can hang off (the bulk service
    // only ever touches the tasks + their assignments / help, but the FK needs a
    // parent subcategory to exist).
    private static async Task<int> SeedSubcategoryAsync(CommunityHubDbContext db, int eventId)
    {
        var cat = new VolunteerCategory { EventId = eventId, Name = "Logistics" };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory { EventId = eventId, CategoryId = cat.Id, Name = "Badges" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();
        return sub.Id;
    }

    private static VolunteerTask Task(
        int eventId, int subId, string title,
        VolunteerTaskStatus status = VolunteerTaskStatus.Open) =>
        new() { EventId = eventId, SubcategoryId = subId, Title = title, Status = status };

    [Fact]
    public async Task ChangeStatus_flips_only_rows_not_already_in_status_and_counts_real_changes()
    {
        using var db = NewDb();
        var subId = await SeedSubcategoryAsync(db, EventId);
        var a = Task(EventId, subId, "A", VolunteerTaskStatus.Open);
        var b = Task(EventId, subId, "B", VolunteerTaskStatus.InProgress);
        var c = Task(EventId, subId, "C", VolunteerTaskStatus.Done); // already target
        db.VolunteerTasks.AddRange(a, b, c);
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        var result = await svc.ChangeStatusAsync(
            EventId, new[] { a.Id, b.Id, c.Id }, VolunteerTaskStatus.Done);

        Assert.Equal(3, result.Matched);
        Assert.Equal(2, result.Changed);   // a + b moved; c was already Done
        Assert.Equal(VolunteerTaskStatus.Done, (await db.VolunteerTasks.FindAsync(a.Id))!.Status);
        Assert.Equal(VolunteerTaskStatus.Done, (await db.VolunteerTasks.FindAsync(b.Id))!.Status);
    }

    [Fact]
    public async Task ChangeStatus_is_idempotent_on_second_run()
    {
        using var db = NewDb();
        var subId = await SeedSubcategoryAsync(db, EventId);
        var a = Task(EventId, subId, "A", VolunteerTaskStatus.Open);
        db.VolunteerTasks.Add(a);
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        var first = await svc.ChangeStatusAsync(EventId, new[] { a.Id }, VolunteerTaskStatus.Cancelled);
        var second = await svc.ChangeStatusAsync(EventId, new[] { a.Id }, VolunteerTaskStatus.Cancelled);

        Assert.Equal(1, first.Changed);
        Assert.Equal(0, second.Changed);   // nothing left to change
        Assert.Equal(1, second.Matched);
    }

    [Fact]
    public async Task ChangeStatus_never_crosses_event_boundaries()
    {
        using var db = NewDb();
        var mineSub = await SeedSubcategoryAsync(db, EventId);
        var theirsSub = await SeedSubcategoryAsync(db, OtherEventId);
        var mine = Task(EventId, mineSub, "mine");
        var theirs = Task(OtherEventId, theirsSub, "theirs");
        db.VolunteerTasks.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        var result = await svc.ChangeStatusAsync(
            EventId, new[] { mine.Id, theirs.Id }, VolunteerTaskStatus.Done);

        Assert.Equal(1, result.Matched);   // only mine resolved in this event
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.Skipped(2));
        Assert.Equal(VolunteerTaskStatus.Done, (await db.VolunteerTasks.FindAsync(mine.Id))!.Status);
        Assert.Equal(VolunteerTaskStatus.Open, (await db.VolunteerTasks.FindAsync(theirs.Id))!.Status); // untouched
    }

    [Fact]
    public async Task Delete_removes_clean_tasks_and_cleans_their_assignments()
    {
        using var db = NewDb();
        var subId = await SeedSubcategoryAsync(db, EventId);
        var a = Task(EventId, subId, "A");
        var b = Task(EventId, subId, "B");
        db.VolunteerTasks.AddRange(a, b);
        await db.SaveChangesAsync();
        // a has a volunteer assignment (import-state placement link, not engagement).
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = a.Id, ParticipantId = 99,
        });
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        var result = await svc.DeleteAsync(EventId, new[] { a.Id, b.Id });

        Assert.Equal(2, result.Matched);
        Assert.Equal(2, result.Deleted);
        Assert.Equal(0, result.Blocked);
        Assert.False(await db.VolunteerTasks.AnyAsync(t => t.Id == a.Id || t.Id == b.Id));
        // The assignment went with its task — no orphan.
        Assert.False(await db.VolunteerTaskAssignments.AnyAsync(x => x.TaskId == a.Id));
    }

    [Fact]
    public async Task Delete_blocks_a_task_with_help_request_history_and_leaves_it_untouched()
    {
        using var db = NewDb();
        var subId = await SeedSubcategoryAsync(db, EventId);
        var clean = Task(EventId, subId, "clean");
        var withHelp = Task(EventId, subId, "withHelp");
        db.VolunteerTasks.AddRange(clean, withHelp);
        await db.SaveChangesAsync();
        db.VolunteerHelpRequests.Add(new VolunteerHelpRequest
        {
            EventId = EventId, TaskId = withHelp.Id,
            // CategoryId is required on the row; the seeded subcategory's category id
            // is not strictly needed for the block probe (it keys on TaskId), but set
            // a non-zero value so the row is realistic.
            CategoryId = 1,
            RequestedByParticipantId = 99,
            Message = "Need a hand at the badge desk.",
            Status = VolunteerHelpStatus.Open,
        });
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        var result = await svc.DeleteAsync(EventId, new[] { clean.Id, withHelp.Id });

        Assert.Equal(2, result.Matched);
        Assert.Equal(1, result.Deleted);   // only the clean one
        Assert.Equal(1, result.Blocked);   // the help-request task is protected
        Assert.False(await db.VolunteerTasks.AnyAsync(t => t.Id == clean.Id));
        Assert.True(await db.VolunteerTasks.AnyAsync(t => t.Id == withHelp.Id));      // untouched
        Assert.True(await db.VolunteerHelpRequests.AnyAsync(h => h.TaskId == withHelp.Id)); // not destroyed
    }

    [Fact]
    public async Task Delete_is_edition_scoped()
    {
        using var db = NewDb();
        var theirsSub = await SeedSubcategoryAsync(db, OtherEventId);
        var theirs = Task(OtherEventId, theirsSub, "theirs");
        db.VolunteerTasks.Add(theirs);
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        var result = await svc.DeleteAsync(EventId, new[] { theirs.Id });

        Assert.Equal(0, result.Matched);   // never found in this edition
        Assert.Equal(0, result.Deleted);
        Assert.True(await db.VolunteerTasks.AnyAsync(t => t.Id == theirs.Id)); // untouched
    }

    [Fact]
    public async Task Empty_invalid_and_duplicate_selections_are_safe()
    {
        using var db = NewDb();
        var subId = await SeedSubcategoryAsync(db, EventId);
        var a = Task(EventId, subId, "A");
        db.VolunteerTasks.Add(a);
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        var empty = await svc.ChangeStatusAsync(EventId, Array.Empty<int>(), VolunteerTaskStatus.Done);
        var bogus = await svc.DeleteAsync(EventId, new[] { 0, -3, 9999 });
        var dupes = await svc.ChangeStatusAsync(EventId, new[] { a.Id, a.Id, a.Id }, VolunteerTaskStatus.Done);

        Assert.Equal(0, empty.Matched);
        Assert.Equal(0, bogus.Matched);
        Assert.Equal(1, dupes.Matched);   // de-duped to one
        Assert.Equal(1, dupes.Changed);
    }
}
