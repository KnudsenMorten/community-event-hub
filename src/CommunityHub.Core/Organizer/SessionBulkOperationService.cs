using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// Organizer-only BULK delete over a selected set of SESSIONS in one edition. The
/// Sessions grid already deletes one row at a time via
/// <see cref="SessionDeletionService"/>; this is the multi-select counterpart for
/// clearing several bad / duplicate sessions at once (a large Sessionize-imported
/// grid is the most common place an organizer needs it).
///
/// It applies EXACTLY the same safe semantics as the single-row
/// <see cref="SessionDeletionService"/>, row by row:
///   - EVERY operation is scoped to the caller's <c>eventId</c>; a session in
///     another edition is never found, never touched.
///   - A session with ATTENDEE ENGAGEMENT (questions / evaluations / master-class
///     bookings) is NOT deleted — it is counted as <see cref="BulkResult.Blocked"/>
///     and left untouched, so attendee-supplied data can never be silently lost.
///   - A clean session has its import-state SPEAKER links cleaned and is removed.
///   - The count of deleted IMPORTED (non-hub-added) sessions is reported so the
///     caller can warn that a Sessionize re-import will recreate them.
///   - One <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> per call —
///     the whole batch commits together or not at all.
///
/// No schema change — it operates on the same entities + links the single-row
/// service does.
/// </summary>
public sealed class SessionBulkOperationService
{
    private readonly CommunityHubDbContext _db;

    public SessionBulkOperationService(CommunityHubDbContext db)
    {
        _db = db;
    }

    /// <summary>Outcome of a bulk session-delete call.</summary>
    /// <param name="Matched">Distinct ids that resolved to a session in this event.</param>
    /// <param name="Deleted">How many clean sessions were physically removed.</param>
    /// <param name="Blocked">
    /// How many sessions were left untouched because they carry attendee engagement
    /// (questions / evaluations / bookings) that must not be destroyed.
    /// </param>
    /// <param name="ImportedDeleted">
    /// Of the deleted sessions, how many came from Sessionize (not hub-added) — these
    /// will be recreated by the next import unless removed there too.
    /// </param>
    public sealed record BulkResult(int Matched, int Deleted, int Blocked, int ImportedDeleted)
    {
        /// <summary>How many requested ids did NOT resolve in this event (ignored).</summary>
        public int Skipped(int requested) => Math.Max(0, requested - Matched);
    }

    /// <summary>
    /// Delete every selected session that is SAFE to delete. Sessions with attendee
    /// engagement are left untouched (counted in <see cref="BulkResult.Blocked"/>);
    /// clean sessions have their speaker links cleaned and are removed. One transaction.
    /// </summary>
    public async Task<BulkResult> DeleteAsync(
        int eventId, IEnumerable<int> sessionIds, CancellationToken ct = default)
    {
        var ids = Normalize(sessionIds);
        if (ids.Count == 0) return new BulkResult(0, 0, 0, 0);

        var targets = await _db.Sessions
            .Include(s => s.SessionSpeakers)
            .Where(s => s.EventId == eventId && ids.Contains(s.Id))
            .ToListAsync(ct);
        if (targets.Count == 0) return new BulkResult(0, 0, 0, 0);

        var targetIds = targets.Select(s => s.Id).ToList();

        // Attendee engagement across the whole selection in three set-based probes
        // (rather than per-row N+1) — a session is blocked if ANY of these has a row.
        var withQuestions = await _db.SessionQuestions
            .Where(q => targetIds.Contains(q.SessionId)).Select(q => q.SessionId)
            .Distinct().ToListAsync(ct);
        var withEvaluations = await _db.SessionEvaluations
            .Where(e => targetIds.Contains(e.SessionId)).Select(e => e.SessionId)
            .Distinct().ToListAsync(ct);
        var withSignups = await _db.MasterClassSignups
            .Where(m => targetIds.Contains(m.SessionId)).Select(m => m.SessionId)
            .Distinct().ToListAsync(ct);

        var blocked = new HashSet<int>(withQuestions);
        blocked.UnionWith(withEvaluations);
        blocked.UnionWith(withSignups);

        var deletable = targets.Where(s => !blocked.Contains(s.Id)).ToList();
        int importedDeleted = 0;
        if (deletable.Count > 0)
        {
            foreach (var s in deletable)
            {
                if (!s.IsHubAdded) importedDeleted++;
                // Speaker links are import-state — clean them with the session.
                if (s.SessionSpeakers.Count > 0)
                    _db.SessionSpeakers.RemoveRange(s.SessionSpeakers);
            }
            _db.Sessions.RemoveRange(deletable);
            await _db.SaveChangesAsync(ct);
        }

        return new BulkResult(
            Matched: targets.Count, Deleted: deletable.Count,
            Blocked: blocked.Count, ImportedDeleted: importedDeleted);
    }

    // Distinct + drop non-positive ids so a stray "0"/duplicate from a posted form
    // never widens the match set.
    private static List<int> Normalize(IEnumerable<int> ids) =>
        ids.Where(id => id > 0).Distinct().ToList();
}
