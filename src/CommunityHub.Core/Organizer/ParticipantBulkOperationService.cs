using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// Organizer-only bulk operations over a selected set of participants in one
/// edition. The Participants grid lets an organizer act on one row at a time
/// (toggle IsActive); this service is the multi-select counterpart — pick many
/// rows, then deactivate / reactivate / change-role in a single call.
///
/// Invariants (all enforced here, not in the page):
///   - EVERY operation is scoped to the caller's <c>eventId</c>; ids that
///     belong to another edition are silently ignored, never touched. An
///     organizer can only ever act inside their own event.
///   - The op is idempotent: deactivating an already-inactive participant (or
///     re-assigning the role a participant already has) changes nothing and is
///     not counted as "changed". <see cref="BulkResult.Changed"/> reflects the
///     real number of rows whose state actually moved.
///   - One <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> per call:
///     the whole batch commits together or not at all.
///
/// The service deliberately operates only on fields that already exist on
/// <see cref="Participant"/> (<see cref="Participant.IsActive"/>,
/// <see cref="Participant.Role"/>) — no schema change. Deactivating here has the
/// same effect as the single-row toggle: an inactive participant can no longer
/// sign in (the PIN flow checks <see cref="Participant.IsActive"/>).
/// </summary>
public sealed class ParticipantBulkOperationService
{
    private readonly CommunityHubDbContext _db;

    public ParticipantBulkOperationService(CommunityHubDbContext db)
    {
        _db = db;
    }

    /// <summary>Outcome of a bulk call.</summary>
    /// <param name="Matched">Distinct ids that resolved to a participant in this event.</param>
    /// <param name="Changed">Of the matched rows, how many actually changed state.</param>
    public sealed record BulkResult(int Matched, int Changed)
    {
        /// <summary>How many requested ids did NOT resolve in this event (ignored).</summary>
        public int Skipped(int requested) => Math.Max(0, requested - Matched);
    }

    /// <summary>Deactivate every selected participant (no-op for already-inactive).</summary>
    public Task<BulkResult> DeactivateAsync(
        int eventId, IEnumerable<int> participantIds, CancellationToken ct = default)
        => SetActiveAsync(eventId, participantIds, active: false, ct);

    /// <summary>Reactivate every selected participant (no-op for already-active).</summary>
    public Task<BulkResult> ReactivateAsync(
        int eventId, IEnumerable<int> participantIds, CancellationToken ct = default)
        => SetActiveAsync(eventId, participantIds, active: true, ct);

    private async Task<BulkResult> SetActiveAsync(
        int eventId, IEnumerable<int> participantIds, bool active, CancellationToken ct)
    {
        var ids = Normalize(participantIds);
        if (ids.Count == 0) return new BulkResult(0, 0);

        var targets = await _db.Participants
            .Where(p => p.EventId == eventId && ids.Contains(p.Id))
            .ToListAsync(ct);

        int changed = 0;
        foreach (var p in targets)
        {
            // Compare against the LIFECYCLE-CORRECT active state, not just the
            // IsActive flag — a synced participant can have IsActive=true while
            // LifecycleState != Active (so it still reads as "Inactive"). Activating
            // must therefore set BOTH; otherwise reactivate said "already active" and
            // never cleared the lifecycle gate (operator 2026-06-23).
            if (ParticipantActivation.IsActive(p) == active) continue;
            if (active)
            {
                p.IsActive = true;
                p.LifecycleState = ParticipantLifecycleState.Active;
            }
            else
            {
                p.IsActive = false; // the withdrawal switch — enough to read inactive
            }
            changed++;
        }

        if (changed > 0) await _db.SaveChangesAsync(ct);
        return new BulkResult(targets.Count, changed);
    }

    /// <summary>
    /// Assign <paramref name="role"/> to every selected participant (no-op for
    /// rows that already have that role). Changing role re-points which hub a
    /// person lands in; it does not touch <see cref="Participant.IsActive"/>.
    /// </summary>
    public async Task<BulkResult> ChangeRoleAsync(
        int eventId, IEnumerable<int> participantIds, ParticipantRole role,
        CancellationToken ct = default)
    {
        var ids = Normalize(participantIds);
        if (ids.Count == 0) return new BulkResult(0, 0);

        var targets = await _db.Participants
            .Where(p => p.EventId == eventId && ids.Contains(p.Id))
            .ToListAsync(ct);

        int changed = 0;
        foreach (var p in targets)
        {
            if (p.Role == role) continue;
            p.Role = role;
            changed++;
        }

        if (changed > 0) await _db.SaveChangesAsync(ct);
        return new BulkResult(targets.Count, changed);
    }

    /// <summary>
    /// Assign rollout <paramref name="ring"/> to every selected participant's own
    /// ring (<see cref="Participant.Ring"/>) — no-op for rows already on that ring
    /// (operator 2026-06-23). Edition-scoped; does not touch IsActive or role.
    /// </summary>
    public async Task<BulkResult> SetRingAsync(
        int eventId, IEnumerable<int> participantIds, Ring ring,
        CancellationToken ct = default)
    {
        var ids = Normalize(participantIds);
        if (ids.Count == 0) return new BulkResult(0, 0);

        var targets = await _db.Participants
            .Where(p => p.EventId == eventId && ids.Contains(p.Id))
            .ToListAsync(ct);

        int changed = 0;
        foreach (var p in targets)
        {
            if (p.Ring == ring) continue;
            p.Ring = ring;
            changed++;
        }

        if (changed > 0) await _db.SaveChangesAsync(ct);
        return new BulkResult(targets.Count, changed);
    }

    // Distinct + drop non-positive ids so a stray "0"/duplicate from a posted
    // form never widens the match set.
    private static List<int> Normalize(IEnumerable<int> ids) =>
        ids.Where(id => id > 0).Distinct().ToList();
}
