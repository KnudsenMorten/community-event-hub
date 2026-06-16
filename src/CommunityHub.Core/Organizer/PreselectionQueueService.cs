using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// Organizer-only operations over the pre-selection queue — the holding area
/// where prospective volunteers / speakers / media-team land (from the
/// Sessionize-API speaker sync and the volunteer interest form) as
/// <see cref="ParticipantLifecycleState.Inactive"/> /
/// <see cref="ParticipantLifecycleState.Preselected"/>. An organizer validates
/// the data and advances rows along the lifecycle
/// <c>Inactive → Preselected → Active</c>, single OR multi-select.
///
/// Invariants (enforced HERE, not in the page):
///   - EVERY operation is scoped to the caller's <c>eventId</c>; ids from
///     another edition are silently ignored, never touched.
///   - The lifecycle only ever moves FORWARD. Advancing to a state a row already
///     reached (or passed) is a no-op and is not counted as "changed", so the
///     enum's int order is the guard (target &gt; current ⇒ change).
///   - One <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> per call:
///     the whole batch commits together or not at all.
///   - Activating a queue row also flips <see cref="Participant.IsActive"/> on,
///     so the combined login gate (IsActive AND lifecycle Active) is satisfied
///     in one step; a prior withdrawal is never silently un-cancelled, because a
///     withdrawn person is removed from the queue by deactivation, not advanced.
/// </summary>
public sealed class PreselectionQueueService
{
    private readonly CommunityHubDbContext _db;

    public PreselectionQueueService(CommunityHubDbContext db)
    {
        _db = db;
    }

    /// <summary>Outcome of a queue advance call.</summary>
    /// <param name="Matched">Distinct ids that resolved to a participant in this event.</param>
    /// <param name="Changed">Of the matched rows, how many actually advanced state.</param>
    /// <param name="ActivatedIds">
    /// The ids that newly reached <see cref="ParticipantLifecycleState.Active"/> in
    /// THIS call (empty unless the target was Active). The hook the onboarding
    /// auto-send (10a-1) fires on — a row already Active is not re-listed, so
    /// onboarding is never double-triggered.
    /// </param>
    public sealed record QueueResult(
        int Matched, int Changed, IReadOnlyList<int> ActivatedIds)
    {
        public QueueResult(int matched, int changed)
            : this(matched, changed, Array.Empty<int>()) { }

        /// <summary>How many requested ids did NOT resolve in this event (ignored).</summary>
        public int Skipped(int requested) => Math.Max(0, requested - Matched);
    }

    /// <summary>
    /// Load the pre-selection queue for an edition: rows not yet fully activated
    /// (Inactive or Preselected), newest first. Optionally filter by inbound
    /// source.
    /// </summary>
    public async Task<IReadOnlyList<Participant>> GetQueueAsync(
        int eventId, ParticipantQueueSource? source = null,
        CancellationToken ct = default)
    {
        var q = _db.Participants
            .Where(p => p.EventId == eventId
                        && p.LifecycleState != ParticipantLifecycleState.Active);
        if (source is not null)
        {
            q = q.Where(p => p.QueueSource == source.Value);
        }
        return await q
            .OrderBy(p => p.LifecycleState)
            .ThenByDescending(p => p.CreatedAt)
            .ThenBy(p => p.FullName)
            .ToListAsync(ct);
    }

    /// <summary>Advance every selected row to <see cref="ParticipantLifecycleState.Preselected"/>.</summary>
    public Task<QueueResult> PreselectAsync(
        int eventId, IEnumerable<int> participantIds, CancellationToken ct = default)
        => AdvanceAsync(eventId, participantIds, ParticipantLifecycleState.Preselected, ct);

    /// <summary>
    /// Advance every selected row to <see cref="ParticipantLifecycleState.Active"/>
    /// (full activation). Activating also turns <see cref="Participant.IsActive"/>
    /// on so the row can immediately sign in.
    /// </summary>
    public Task<QueueResult> ActivateAsync(
        int eventId, IEnumerable<int> participantIds, CancellationToken ct = default)
        => AdvanceAsync(eventId, participantIds, ParticipantLifecycleState.Active, ct);

    /// <summary>
    /// Advance the selected rows to <paramref name="target"/>. Forward-only: a
    /// row already at or beyond the target is untouched. When the target is
    /// <see cref="ParticipantLifecycleState.Active"/> the row is also marked
    /// <see cref="Participant.IsActive"/> = true.
    /// </summary>
    public async Task<QueueResult> AdvanceAsync(
        int eventId, IEnumerable<int> participantIds,
        ParticipantLifecycleState target, CancellationToken ct = default)
    {
        var ids = Normalize(participantIds);
        if (ids.Count == 0) return new QueueResult(0, 0);

        var targets = await _db.Participants
            .Where(p => p.EventId == eventId && ids.Contains(p.Id))
            .ToListAsync(ct);

        int changed = 0;
        var activatedIds = new List<int>();
        foreach (var p in targets)
        {
            // Forward-only: never demote, never re-count a no-op.
            if ((int)p.LifecycleState >= (int)target) continue;

            p.LifecycleState = target;
            if (target == ParticipantLifecycleState.Active)
            {
                // Satisfy the combined login gate in one step.
                p.IsActive = true;
                activatedIds.Add(p.Id);   // newly Active -> onboarding hook
            }
            changed++;
        }

        if (changed > 0) await _db.SaveChangesAsync(ct);
        return new QueueResult(targets.Count, changed, activatedIds);
    }

    // Distinct + drop non-positive ids so a stray "0"/duplicate from a posted
    // form never widens the match set.
    private static List<int> Normalize(IEnumerable<int> ids) =>
        ids.Where(id => id > 0).Distinct().ToList();
}
