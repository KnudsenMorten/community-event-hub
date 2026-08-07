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

    // ---------------------------------------------------------------- §942 bulk MOVE

    /// <summary>
    /// Seeds a SECOND category+subcategory in the same event, so a move has somewhere real to go.
    /// </summary>
    private static async Task<int> SeedSecondSubcategoryAsync(
        CommunityHubDbContext db, int eventId, string catName = "Check-in", string subName = "Desk")
    {
        var cat = new VolunteerCategory { EventId = eventId, Name = catName };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory { EventId = eventId, CategoryId = cat.Id, Name = subName };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();
        return sub.Id;
    }

    /// <summary>
    /// §942 — the headline case. Operator 2026-08-07: the Excel import put all 127 tasks in one
    /// bucket while "Check-in" — which already had a lead and a supervisor appointed — held zero.
    /// </summary>
    [Fact]
    public async Task Move_reparents_the_selected_tasks_and_counts_only_real_moves()
    {
        using var db = NewDb();
        var bucket = await SeedSubcategoryAsync(db, EventId);
        var checkIn = await SeedSecondSubcategoryAsync(db, EventId);
        var a = Task(EventId, bucket, "Check-in desk morning");
        var b = Task(EventId, bucket, "Check-in desk afternoon");
        var already = Task(EventId, checkIn, "Already in check-in");
        db.VolunteerTasks.AddRange(a, b, already);
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        var result = await svc.MoveAsync(EventId, new[] { a.Id, b.Id, already.Id }, checkIn);

        Assert.Equal(3, result.Matched);
        Assert.Equal(2, result.Moved);
        Assert.Equal(1, result.AlreadyThere);   // idempotent, like ChangeStatus
        Assert.Equal(checkIn, (await db.VolunteerTasks.FindAsync(a.Id))!.SubcategoryId);
        Assert.Equal(checkIn, (await db.VolunteerTasks.FindAsync(b.Id))!.SubcategoryId);
    }

    /// <summary>
    /// 🔴 THE GUARANTEE §942 NAMES EXPLICITLY: <i>"assignments must survive the move"</i>. A
    /// volunteer already placed on a task keeps that placement — re-parenting is an organisational
    /// change, not a staffing one. Asserted rather than assumed, because "the code does not touch
    /// assignments" is a claim about today's code, not a guarantee about tomorrow's.
    /// </summary>
    [Fact]
    public async Task Move_keeps_every_volunteer_assignment()
    {
        using var db = NewDb();
        var bucket = await SeedSubcategoryAsync(db, EventId);
        var checkIn = await SeedSecondSubcategoryAsync(db, EventId);
        var t = Task(EventId, bucket, "Check-in desk");
        db.VolunteerTasks.Add(t);
        await db.SaveChangesAsync();
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = t.Id, ParticipantId = 4242,
        });
        await db.SaveChangesAsync();

        await new VolunteerTaskBulkOperationService(db).MoveAsync(EventId, new[] { t.Id }, checkIn);

        var kept = await db.VolunteerTaskAssignments
            .Where(x => x.TaskId == t.Id).ToListAsync();
        Assert.Single(kept);
        Assert.Equal(4242, kept[0].ParticipantId);
        Assert.Equal(checkIn, (await db.VolunteerTasks.FindAsync(t.Id))!.SubcategoryId);
    }

    /// <summary>
    /// 🔒 Event-scoped like every other operation here: another edition's task is not ours to move,
    /// even if its id is posted.
    /// </summary>
    [Fact]
    public async Task Move_ignores_a_task_from_another_event()
    {
        using var db = NewDb();
        var mine = await SeedSubcategoryAsync(db, EventId);
        var target = await SeedSecondSubcategoryAsync(db, EventId);
        var theirSub = await SeedSubcategoryAsync(db, OtherEventId);
        var theirs = Task(OtherEventId, theirSub, "Not mine");
        db.VolunteerTasks.Add(theirs);
        await db.SaveChangesAsync();

        var result = await new VolunteerTaskBulkOperationService(db)
            .MoveAsync(EventId, new[] { theirs.Id }, target);

        Assert.Equal(0, result.Matched);
        Assert.Equal(1, result.Skipped(1));
        Assert.Equal(theirSub, (await db.VolunteerTasks.FindAsync(theirs.Id))!.SubcategoryId);
    }

    /// <summary>
    /// 🔴 The TARGET is validated too, and this is the dangerous direction: a subcategory id from
    /// another edition would relocate this edition's work into somebody else's structure. Nothing
    /// moves, and it says so rather than failing silently.
    /// </summary>
    [Fact]
    public async Task Move_refuses_a_target_subcategory_from_another_event()
    {
        using var db = NewDb();
        var mine = await SeedSubcategoryAsync(db, EventId);
        var foreignTarget = await SeedSubcategoryAsync(db, OtherEventId);
        var t = Task(EventId, mine, "Mine");
        db.VolunteerTasks.Add(t);
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MoveAsync(EventId, new[] { t.Id }, foreignTarget));

        Assert.Equal(mine, (await db.VolunteerTasks.FindAsync(t.Id))!.SubcategoryId);
    }

    [Fact]
    public async Task Move_refuses_an_unknown_target_and_moves_nothing()
    {
        using var db = NewDb();
        var mine = await SeedSubcategoryAsync(db, EventId);
        var t = Task(EventId, mine, "Mine");
        db.VolunteerTasks.Add(t);
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MoveAsync(EventId, new[] { t.Id }, 999_999));

        Assert.Equal(mine, (await db.VolunteerTasks.FindAsync(t.Id))!.SubcategoryId);
    }

    /// <summary>
    /// 🔑 MOVE is REVERSIBLE — that is the property that made it the right reading of his
    /// "link (or MOVE)", and the reason it needs no schema change. Moving back restores the
    /// original state exactly, assignments included.
    /// </summary>
    [Fact]
    public async Task Move_is_reversible()
    {
        using var db = NewDb();
        var bucket = await SeedSubcategoryAsync(db, EventId);
        var checkIn = await SeedSecondSubcategoryAsync(db, EventId);
        var t = Task(EventId, bucket, "Check-in desk");
        db.VolunteerTasks.Add(t);
        await db.SaveChangesAsync();
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = t.Id, ParticipantId = 7,
        });
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        await svc.MoveAsync(EventId, new[] { t.Id }, checkIn);
        await svc.MoveAsync(EventId, new[] { t.Id }, bucket);

        Assert.Equal(bucket, (await db.VolunteerTasks.FindAsync(t.Id))!.SubcategoryId);
        Assert.Single(await db.VolunteerTaskAssignments.Where(x => x.TaskId == t.Id).ToListAsync());
    }

    [Fact]
    public async Task Move_with_no_selection_does_nothing()
    {
        using var db = NewDb();
        var target = await SeedSubcategoryAsync(db, EventId);

        var result = await new VolunteerTaskBulkOperationService(db)
            .MoveAsync(EventId, Array.Empty<int>(), target);

        Assert.Equal(0, result.Matched);
        Assert.Equal(0, result.Moved);
    }

    // ------------------------------------------------ §942 move into a whole CATEGORY

    /// <summary>
    /// 🔴 THE CASE HE ACTUALLY ASKED FOR — <i>"move all tasks related to check-in to that
    /// CATEGORY"</i> — and the one the sub-category-only version could not do. On the first live run
    /// the move dropdown offered exactly ONE target, because Check-in had **no sub-categories** and
    /// tasks hang off sub-categories. §942's own table said so ("Check-in: 0 subcategories, 0
    /// tasks") and the implication was missed until the page was looked at.
    /// </summary>
    [Fact]
    public async Task Move_to_a_category_with_no_subcategory_creates_one_and_lands_there()
    {
        using var db = NewDb();
        var bucket = await SeedSubcategoryAsync(db, EventId);
        var emptyCat = new VolunteerCategory { EventId = EventId, Name = "Check-in" };
        db.VolunteerCategories.Add(emptyCat);
        await db.SaveChangesAsync();

        var t = Task(EventId, bucket, "Check-in desk morning");
        db.VolunteerTasks.Add(t);
        await db.SaveChangesAsync();

        var (result, landedIn) = await new VolunteerTaskBulkOperationService(db)
            .MoveToCategoryAsync(EventId, new[] { t.Id }, emptyCat.Id);

        Assert.Equal(1, result.Moved);
        Assert.Equal("Check-in → General", landedIn);

        var created = await db.VolunteerSubcategories
            .SingleAsync(s => s.CategoryId == emptyCat.Id);
        Assert.Equal("General", created.Name);
        Assert.Equal(created.Id, (await db.VolunteerTasks.FindAsync(t.Id))!.SubcategoryId);
    }

    /// <summary>
    /// 🔒 It creates a container only when there is none. A category that already has sub-categories
    /// must not sprout a "General" beside them every time somebody moves work in.
    /// </summary>
    [Fact]
    public async Task Move_to_a_category_that_has_subcategories_reuses_the_first_and_creates_nothing()
    {
        using var db = NewDb();
        var bucket = await SeedSubcategoryAsync(db, EventId);
        var desk = await SeedSecondSubcategoryAsync(db, EventId, "Check-in", "Desk");
        var catId = (await db.VolunteerSubcategories.FindAsync(desk))!.CategoryId;

        var t = Task(EventId, bucket, "Check-in desk morning");
        db.VolunteerTasks.Add(t);
        await db.SaveChangesAsync();

        var (result, landedIn) = await new VolunteerTaskBulkOperationService(db)
            .MoveToCategoryAsync(EventId, new[] { t.Id }, catId);

        Assert.Equal(1, result.Moved);
        Assert.Equal("Check-in → Desk", landedIn);
        Assert.Equal(desk, (await db.VolunteerTasks.FindAsync(t.Id))!.SubcategoryId);
        Assert.Single(await db.VolunteerSubcategories.Where(s => s.CategoryId == catId).ToListAsync());
    }

    /// <summary>🔒 Same event-scoping guarantee as the sub-category target.</summary>
    [Fact]
    public async Task Move_refuses_a_target_category_from_another_event()
    {
        using var db = NewDb();
        var mine = await SeedSubcategoryAsync(db, EventId);
        var foreignCat = new VolunteerCategory { EventId = OtherEventId, Name = "Theirs" };
        db.VolunteerCategories.Add(foreignCat);
        await db.SaveChangesAsync();
        var t = Task(EventId, mine, "Mine");
        db.VolunteerTasks.Add(t);
        await db.SaveChangesAsync();

        var svc = new VolunteerTaskBulkOperationService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.MoveToCategoryAsync(EventId, new[] { t.Id }, foreignCat.Id));

        Assert.Equal(mine, (await db.VolunteerTasks.FindAsync(t.Id))!.SubcategoryId);
        // And it did NOT leave a stray container behind in the other edition.
        Assert.Empty(await db.VolunteerSubcategories.Where(s => s.CategoryId == foreignCat.Id).ToListAsync());
    }
}
