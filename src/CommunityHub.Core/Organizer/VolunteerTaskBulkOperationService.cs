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

    // Distinct + drop non-positive ids so a stray "0"/duplicate from a posted form
    // never widens the match set.
    private static List<int> Normalize(IEnumerable<int> ids) =>
        ids.Where(id => id > 0).Distinct().ToList();
}
