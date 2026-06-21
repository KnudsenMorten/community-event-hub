using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// The go-live cleanup authority (REQUIREMENTS § 1 "Go-live test-data cleanup").
/// Once the synthetic test cast has done its job, the organizer must be able to
/// clear it out before the edition goes live so test rows never skew real counts
/// or receive real comms. Every synthetic row carries
/// <see cref="Participant.IsTestUser"/> = true (set when the seed is planted), so
/// cleanup is exactly "remove / deactivate everyone WHERE IsTestUser = true".
///
/// It does NOT invent its own deletion rules: it reuses the proven, safe
/// <see cref="ParticipantDeletionService"/> per row — a clean test row is
/// hard-deleted, a test row that picked up engagement during testing (e.g. a test
/// speaker linked to a session) is DEACTIVATED instead of destroyed, so cleanup
/// can never orphan real data. The result reports both outcomes honestly.
///
/// Edition-scoped: only the caller's edition is ever touched; a test row in
/// another edition is never seen. Read-only <see cref="PreviewAsync"/> lets the
/// organizer see exactly who would go before they commit. No schema change.
/// </summary>
public sealed class TestDataCleanupService
{
    private readonly CommunityHubDbContext _db;
    private readonly ParticipantDeletionService _deletion;

    public TestDataCleanupService(
        CommunityHubDbContext db, ParticipantDeletionService deletion)
    {
        _db = db;
        _deletion = deletion;
    }

    /// <summary>A single test row in the cleanup preview.</summary>
    /// <param name="ParticipantId">The row id.</param>
    /// <param name="FullName">Display name for the confirmation list.</param>
    /// <param name="Email">Email, for disambiguation.</param>
    /// <param name="Role">The persona, so the organizer sees the spread.</param>
    /// <param name="WouldHardDelete">
    /// True = a clean row that will be physically removed; false = a row with
    /// engagement that will be DEACTIVATED instead (kept, but signed out + hidden).
    /// </param>
    public sealed record TestRow(
        int ParticipantId, string FullName, string Email,
        ParticipantRole Role, bool WouldHardDelete);

    /// <summary>What a cleanup WOULD do, computed without changing anything.</summary>
    /// <param name="Rows">Every test row in the edition (empty = nothing to clean).</param>
    public sealed record CleanupPreview(IReadOnlyList<TestRow> Rows)
    {
        public int Total => Rows.Count;
        public int WouldHardDelete => Rows.Count(r => r.WouldHardDelete);
        public int WouldDeactivate => Rows.Count(r => !r.WouldHardDelete);
        public bool Any => Rows.Count > 0;
    }

    /// <summary>The outcome of an executed cleanup.</summary>
    /// <param name="HardDeleted">Clean test rows physically removed.</param>
    /// <param name="Deactivated">Test rows with engagement that were deactivated instead.</param>
    public sealed record CleanupResult(int HardDeleted, int Deactivated)
    {
        public int Total => HardDeleted + Deactivated;
    }

    /// <summary>
    /// Read-only preview: every <see cref="Participant.IsTestUser"/> row in this
    /// edition, each flagged with whether it would be hard-deleted or only
    /// deactivated (because it has engagement). Never writes.
    /// </summary>
    public async Task<CleanupPreview> PreviewAsync(
        int eventId, CancellationToken ct = default)
    {
        var testers = await _db.Participants
            .Where(p => p.EventId == eventId && p.IsTestUser)
            .OrderBy(p => p.Role).ThenBy(p => p.FullName)
            .Select(p => new { p.Id, p.FullName, p.Email, p.Role })
            .ToListAsync(ct);

        var rows = new List<TestRow>(testers.Count);
        foreach (var t in testers)
        {
            var blockers = await _deletion.GetHardDeleteBlockersAsync(eventId, t.Id, ct);
            rows.Add(new TestRow(
                t.Id, t.FullName, t.Email, t.Role, WouldHardDelete: blockers.Count == 0));
        }
        return new CleanupPreview(rows);
    }

    /// <summary>
    /// Execute the cleanup: for every test row in the edition, hard-delete it when
    /// safe, otherwise deactivate it. Idempotent — a second run finds whatever the
    /// first left (the deactivated ones stay until their engagement is cleared) and
    /// re-applies the same safe outcome; once everything clean is gone it is a no-op.
    /// </summary>
    public async Task<CleanupResult> CleanupAsync(
        int eventId, CancellationToken ct = default)
    {
        // Snapshot the ids first — deleting inside an active query enumeration is
        // unsafe, and a hard-delete mutates the set we're iterating.
        var ids = await _db.Participants
            .Where(p => p.EventId == eventId && p.IsTestUser)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var hardDeleted = 0;
        var deactivated = 0;
        foreach (var id in ids)
        {
            // Hard-delete when safe; the service refuses (returns HardDeleteBlocked)
            // for a row with engagement, in which case we deactivate instead so a
            // test row can never leave real data orphaned.
            var hd = await _deletion.HardDeleteAsync(eventId, id, ct);
            if (hd.Status == ParticipantDeletionService.DeletionStatus.HardDeleted)
            {
                hardDeleted++;
                continue;
            }

            var da = await _deletion.DeactivateAsync(eventId, id, ct);
            if (da.Status == ParticipantDeletionService.DeletionStatus.Deactivated
                || da.Status == ParticipantDeletionService.DeletionStatus.AlreadyInactive)
            {
                deactivated++;
            }
        }

        return new CleanupResult(hardDeleted, deactivated);
    }
}
