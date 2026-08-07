using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// Organizer-only BULK operations over a selected set of volunteer work-structure
/// TASKS in one edition. The volunteer structure already has full single-row CRUD
/// (<see cref="VolunteerStructureService"/>); this is the multi-select counterpart
/// the organizer needs when building a rota of dozens-to-hundreds of tasks — pick
/// many rows, then change their status, or delete the clean ones, in one call.
///
/// It mirrors the established safe semantics:
///   - <see cref="ParticipantBulkOperationService"/> for the change-status batch
///     (event-scoped, idempotent, honest changed-count, one SaveChanges), and
///   - <see cref="SessionDeletionService"/> / <see cref="ParticipantDeletionService"/>
///     for delete-safety: a task whose loss would destroy real coordination history
///     (help requests raised by volunteers) is NOT deleted; a clean task is removed
///     with its import-state volunteer ASSIGNMENTS (those are placement links, not
///     engagement, so they go with it — never orphaned).
///
/// Invariants (all enforced HERE, not in the page):
///   - EVERY operation is scoped to the caller's <c>eventId</c>; a task id that
///     belongs to another edition is silently ignored, never touched.
///   - Status change is idempotent: setting a task to the status it already has
///     changes nothing and is not counted as "changed".
///   - Delete is linked-data-safe: tasks with help-request history are reported as
///     <see cref="BulkDeleteResult.Blocked"/> (left untouched); clean tasks have
///     their assignments cleaned then are removed.
///   - One <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> per call —
///     the whole batch commits together or not at all.
///
/// Operates only on fields/links that already exist on <see cref="VolunteerTask"/>
/// and <see cref="VolunteerTaskAssignment"/> — no schema change.
/// </summary>
public sealed class VolunteerTaskBulkOperationService
{
    private readonly CommunityHubDbContext _db;

    public VolunteerTaskBulkOperationService(CommunityHubDbContext db)
    {
        _db = db;
    }

    /// <summary>Outcome of a bulk status-change call.</summary>
    /// <param name="Matched">Distinct ids that resolved to a task in this event.</param>
    /// <param name="Changed">Of the matched rows, how many actually changed status.</param>
    public sealed record BulkResult(int Matched, int Changed)
    {
        /// <summary>How many requested ids did NOT resolve in this event (ignored).</summary>
        public int Skipped(int requested) => Math.Max(0, requested - Matched);
    }

    /// <summary>Outcome of a bulk delete call.</summary>
    /// <param name="Matched">Distinct ids that resolved to a task in this event.</param>
    /// <param name="Deleted">How many clean tasks were physically removed.</param>
    /// <param name="Blocked">
    /// How many tasks were left untouched because they carry coordination history
    /// (help requests) that must not be silently destroyed.
    /// </param>
    public sealed record BulkDeleteResult(int Matched, int Deleted, int Blocked)
    {
        /// <summary>How many requested ids did NOT resolve in this event (ignored).</summary>
        public int Skipped(int requested) => Math.Max(0, requested - Matched);
    }

    /// <summary>
    /// Set every selected task to <paramref name="status"/> (no-op for rows already
    /// in that status). This is the bulk equivalent of marking a slice of the rota
    /// Done / Cancelled / re-Open at once. Does not touch assignments or anything else.
    /// </summary>
    public async Task<BulkResult> ChangeStatusAsync(
        int eventId, IEnumerable<int> taskIds, VolunteerTaskStatus status,
        CancellationToken ct = default)
    {
        var ids = Normalize(taskIds);
        if (ids.Count == 0) return new BulkResult(0, 0);

        var targets = await _db.VolunteerTasks
            .Where(t => t.EventId == eventId && ids.Contains(t.Id))
            .ToListAsync(ct);

        int changed = 0;
        foreach (var t in targets)
        {
            if (t.Status == status) continue;
            t.Status = status;
            t.UpdatedAt = DateTimeOffset.UtcNow;
            changed++;
        }

        if (changed > 0) await _db.SaveChangesAsync(ct);
        return new BulkResult(targets.Count, changed);
    }

    /// <summary>
    /// Delete every selected task that is SAFE to delete. A task with help-request
    /// history is left untouched (counted in <see cref="BulkDeleteResult.Blocked"/>)
    /// so coordination history is never silently lost; a clean task has its
    /// volunteer assignments cleaned (import-state links) and is then removed. The
    /// whole batch is one transaction.
    /// </summary>
    public async Task<BulkDeleteResult> DeleteAsync(
        int eventId, IEnumerable<int> taskIds, CancellationToken ct = default)
    {
        var ids = Normalize(taskIds);
        if (ids.Count == 0) return new BulkDeleteResult(0, 0, 0);

        var targets = await _db.VolunteerTasks
            .Where(t => t.EventId == eventId && ids.Contains(t.Id))
            .ToListAsync(ct);
        if (targets.Count == 0) return new BulkDeleteResult(0, 0, 0);

        var targetIds = targets.Select(t => t.Id).ToList();

        // Coordination history that makes a delete unsafe: any help request raised
        // against the task (a volunteer asked for help / a supervisor answered).
        var blockedTaskIds = await _db.VolunteerHelpRequests
            .Where(h => h.EventId == eventId && targetIds.Contains(h.TaskId))
            .Select(h => h.TaskId)
            .Distinct()
            .ToListAsync(ct);
        var blocked = new HashSet<int>(blockedTaskIds);

        var deletable = targets.Where(t => !blocked.Contains(t.Id)).ToList();
        if (deletable.Count > 0)
        {
            var deletableIds = deletable.Select(t => t.Id).ToList();

            // Assignments are import-state placement links, not engagement — clean
            // them first so the single SaveChanges removes task + links atomically.
            var assignments = await _db.VolunteerTaskAssignments
                .Where(a => a.EventId == eventId && deletableIds.Contains(a.TaskId))
                .ToListAsync(ct);
            if (assignments.Count > 0)
                _db.VolunteerTaskAssignments.RemoveRange(assignments);

            _db.VolunteerTasks.RemoveRange(deletable);
            await _db.SaveChangesAsync(ct);
        }

        return new BulkDeleteResult(
            Matched: targets.Count, Deleted: deletable.Count, Blocked: blocked.Count);
    }

    /// <summary>Outcome of a bulk move.</summary>
    /// <param name="Matched">Distinct ids that resolved to a task in this event.</param>
    /// <param name="Moved">Of those, how many actually changed subcategory.</param>
    /// <param name="AlreadyThere">Matched tasks that were already in the target (no-ops).</param>
    public sealed record BulkMoveResult(int Matched, int Moved, int AlreadyThere)
    {
        /// <summary>How many requested ids did NOT resolve in this event (ignored).</summary>
        public int Skipped(int requested) => Math.Max(0, requested - Matched);
    }

    /// <summary>
    /// §942 — RE-PARENT every selected task under <paramref name="targetSubcategoryId"/>.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-07: <i>"we have all the imported tasks from the excel file, but they
    /// have landed in one bucket: ELDK-Volunteers. We need a MOVE button so we can link (or MOVE) a
    /// group of tasks related to the category"</i>. His structure existed and the work did not live
    /// in it: Check-in had a lead and a supervisor appointed and <b>0 tasks</b>, while the import
    /// bucket held 127.</para>
    ///
    /// <para>🔑 <b>MOVE, not LINK — and it is a one-line data change because of it.</b> He said
    /// "link (or MOVE)"; MOVE is the reading that matches <i>"move all tasks related to check-in to
    /// that category"</i>, and a task's home is already a single <see cref="VolunteerTask.SubcategoryId"/>,
    /// so re-parenting needs <b>no schema change</b> and is fully reversible — move them back. LINK
    /// (one task under several categories) is a many-to-many model that would change how coverage is
    /// counted everywhere it is read, and it is not undone by moving anything back.</para>
    ///
    /// <para>🔒 <b>ASSIGNMENTS SURVIVE.</b> A volunteer already placed on a task keeps that placement:
    /// assignments hang off the TASK, not off its category, so re-parenting does not touch them and
    /// this method deliberately does not go near
    /// <see cref="VolunteerTaskAssignment"/>. That is the difference between this and delete, where
    /// the links are cleaned on purpose — and it is asserted in the tests, because "we did not write
    /// that code" is not the same guarantee as "we checked".</para>
    ///
    /// <para>⚠️ <b>The target is validated, not trusted.</b> A subcategory id from another edition
    /// would silently relocate this edition's work into somebody else's structure, so an unknown or
    /// foreign target moves NOTHING and says so.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The target subcategory does not exist in this event.
    /// </exception>
    /// <summary>
    /// §942 — move into a whole CATEGORY, resolving (or creating) the sub-category to land in.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>This is the call his actual sentence needs.</b> He said <i>"move all tasks related
    /// to check-in to that <b>category</b>"</i> — but tasks hang off a SUB-category, and the first
    /// live run of the sub-category-only version offered exactly ONE target, because <b>Check-in had
    /// zero sub-categories</b>. §942 recorded that in its own table ("Check-in: 0 subcategories, 0
    /// tasks") and the implication was missed: the feature existed and could not do the thing it was
    /// built for.</para>
    ///
    /// <para>🔑 Lands in the category's FIRST sub-category by name, or creates a <c>General</c> one
    /// when it has none. Creating is the honest behaviour here: the alternative is refusing the move
    /// and telling an organizer holding 127 tasks to go and make a container first, which is the
    /// tedium this feature exists to remove.</para>
    /// </remarks>
    /// <returns>The move result, plus the sub-category actually used.</returns>
    public async Task<(BulkMoveResult Result, string LandedIn)> MoveToCategoryAsync(
        int eventId, IEnumerable<int> taskIds, int targetCategoryId,
        CancellationToken ct = default)
    {
        var category = await _db.VolunteerCategories
            .FirstOrDefaultAsync(c => c.Id == targetCategoryId && c.EventId == eventId, ct);
        if (category is null)
            throw new InvalidOperationException("That target category is not part of this event.");

        var sub = await _db.VolunteerSubcategories
            .Where(s => s.EventId == eventId && s.CategoryId == targetCategoryId)
            .OrderBy(s => s.Name)
            .FirstOrDefaultAsync(ct);

        if (sub is null)
        {
            sub = new VolunteerSubcategory
            {
                EventId = eventId, CategoryId = targetCategoryId, Name = "General",
                Description = "Created automatically to receive moved tasks.",
            };
            _db.VolunteerSubcategories.Add(sub);
            await _db.SaveChangesAsync(ct);
        }

        var result = await MoveAsync(eventId, taskIds, sub.Id, ct);
        return (result, $"{category.Name} → {sub.Name}");
    }

    public async Task<BulkMoveResult> MoveAsync(
        int eventId, IEnumerable<int> taskIds, int targetSubcategoryId,
        CancellationToken ct = default)
    {
        var ids = Normalize(taskIds);
        if (ids.Count == 0) return new BulkMoveResult(0, 0, 0);

        var targetExists = await _db.VolunteerSubcategories
            .AnyAsync(s => s.Id == targetSubcategoryId && s.EventId == eventId, ct);
        if (!targetExists)
            throw new InvalidOperationException("That target sub-category is not part of this event.");

        var targets = await _db.VolunteerTasks
            .Where(t => t.EventId == eventId && ids.Contains(t.Id))
            .ToListAsync(ct);

        int moved = 0, already = 0;
        foreach (var t in targets)
        {
            // Idempotent, like ChangeStatusAsync: moving a task to where it already is
            // is not a change, and must not be reported as one.
            if (t.SubcategoryId == targetSubcategoryId) { already++; continue; }
            t.SubcategoryId = targetSubcategoryId;
            t.UpdatedAt = DateTimeOffset.UtcNow;
            moved++;
        }

        if (moved > 0) await _db.SaveChangesAsync(ct);
        return new BulkMoveResult(targets.Count, moved, already);
    }

    // Distinct + drop non-positive ids so a stray "0"/duplicate from a posted form
    // never widens the match set.
    private static List<int> Normalize(IEnumerable<int> ids) =>
        ids.Where(id => id > 0).Distinct().ToList();
}
