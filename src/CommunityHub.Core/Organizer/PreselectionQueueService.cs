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
/// <c>Inactive → Preselected → Active</c>, single OR multi-select. §245: SPEAKERS
/// skip a manual Preselect step (their pre-selection happens in Sessionize) — the
/// Sessionize import lands them directly as Preselected (§299 6.1) and a
/// Preselect batch leaves speaker rows untouched; an organizer activates them in
/// one step ONCE their <see cref="SpeakerProfile.Category"/> is set (the §299 6.1
/// activation hard gate below).
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

    /// <summary>
    /// §299 6.1 activation hard gate — the refusal message for a speaker whose
    /// <see cref="SpeakerProfile.Category"/> is not set yet.
    /// </summary>
    public const string UncategorizedSpeakerMessage =
        "Set the speaker's category (Community / Sponsor / Guest) before activating "
        + "— uncategorized speakers are excluded from all counts.";

    /// <summary>Outcome of a queue advance call.</summary>
    /// <param name="Matched">Distinct ids that resolved to a participant in this event.</param>
    /// <param name="Changed">Of the matched rows, how many actually advanced state.</param>
    /// <param name="ActivatedIds">
    /// The ids that newly reached <see cref="ParticipantLifecycleState.Active"/> in
    /// THIS call (empty unless the target was Active). The hook the onboarding
    /// auto-send (10a-1) fires on — a row already Active is not re-listed, so
    /// onboarding is never double-triggered.
    /// </param>
    /// <param name="RefusedUncategorizedSpeakerIds">
    /// §299 6.1 HARD GATE: speaker rows that were REFUSED activation because their
    /// <see cref="SpeakerProfile.Category"/> is not set (or they have no profile at
    /// all). Empty unless the target was Active. The caller must surface
    /// <see cref="UncategorizedSpeakerMessage"/> for these rows.
    /// </param>
    public sealed record QueueResult(
        int Matched, int Changed, IReadOnlyList<int> ActivatedIds,
        IReadOnlyList<int> RefusedUncategorizedSpeakerIds)
    {
        public QueueResult(int matched, int changed)
            : this(matched, changed, Array.Empty<int>(), Array.Empty<int>()) { }

        public QueueResult(int matched, int changed, IReadOnlyList<int> activatedIds)
            : this(matched, changed, activatedIds, Array.Empty<int>()) { }

        /// <summary>How many requested ids did NOT resolve in this event (ignored).</summary>
        public int Skipped(int requested) => Math.Max(0, requested - Matched);
    }

    /// <summary>
    /// 🔒 §756 — THE ONE DEFINITION of "who is in the pre-selection queue": an edition's
    /// <b>VOLUNTEERS</b> who are not yet fully activated. Every reader must go through this.
    /// </summary>
    /// <remarks>
    /// <para><b>VOLUNTEERS ONLY</b> (operator 2026-08-01: <i>"preselection queue must ONLY contain
    /// volunteers as all other roles are selected and have their own onboarding"</i>). This replaces
    /// the old "everyone except sponsors" scope, which listed speakers, attendees and media-team too
    /// — he saw 6 inactive speakers and 2 attendees sitting in a queue none of them belong in:
    /// <i>"Speakers will NEWER hit that queue"</i>.</para>
    ///
    /// <para>🔑 Every other role is <b>selected elsewhere and has its own door</b>, so nothing is
    /// hidden by narrowing this one: speakers → <c>/Organizer/PendingSpeakers</c> (which also sets
    /// category + ring, so it is the better surface anyway), attendees → reconciled from ticket
    /// orders and managed in <c>/Organizer/Attendees</c>, sponsors → the sponsor admin area
    /// (already excluded here since 2026-06-21). All of them remain reachable in
    /// <c>/Organizer/Participants</c> with the inactive filter.</para>
    ///
    /// <para>🔒 <b>Exposed as a shared predicate, not duplicated.</b> §759: the ops digest had its
    /// own copy of this WHERE clause with a comment saying "mirror GetQueueAsync's scope" — two
    /// definitions of one population, which is exactly how he ended up with a mail announcing
    /// "10 awaiting review" over a queue he then found empty. A mirror is not a shared definition.
    /// Anything that counts or lists this queue calls THIS method.</para>
    /// </remarks>
    public static IQueryable<Participant> QueueRows(IQueryable<Participant> participants, int eventId) =>
        participants.Where(p => p.EventId == eventId
                                && p.LifecycleState != ParticipantLifecycleState.Active
                                && p.Role == ParticipantRole.Volunteer);

    /// <summary>
    /// Load the pre-selection queue for an edition: volunteers not yet fully activated
    /// (Inactive or Preselected), newest first. Optionally filter by inbound source.
    /// </summary>
    public async Task<IReadOnlyList<Participant>> GetQueueAsync(
        int eventId, ParticipantQueueSource? source = null,
        CancellationToken ct = default)
    {
        var q = QueueRows(_db.Participants, eventId);
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

        // §299 6.1 HARD GATE: a speaker cannot be ACTIVATED before an organizer
        // sets their SpeakerCategory — an uncategorized speaker contributes
        // nothing to any count, so silently activating one would corrupt every
        // tally. Applies at activation time only (already-active speakers are
        // untouched). A speaker with no profile row at all is equally
        // uncategorized. Computed as the CATEGORIZED set so a missing profile
        // refuses too.
        var categorizedSpeakerIds = new HashSet<int>();
        if (target == ParticipantLifecycleState.Active)
        {
            var speakerIds = targets
                .Where(p => p.Role == ParticipantRole.Speaker)
                .Select(p => p.Id)
                .ToList();
            if (speakerIds.Count > 0)
            {
                categorizedSpeakerIds = (await _db.SpeakerProfiles
                        .Where(s => s.EventId == eventId
                                    && speakerIds.Contains(s.ParticipantId)
                                    && s.Category != null)
                        .Select(s => s.ParticipantId)
                        .ToListAsync(ct))
                    .ToHashSet();
            }
        }

        int changed = 0;
        var activatedIds = new List<int>();
        var refusedIds = new List<int>();
        foreach (var p in targets)
        {
            // Forward-only: never demote, never re-count a no-op.
            if ((int)p.LifecycleState >= (int)target) continue;

            // §299 6.1: refuse to activate an uncategorized speaker (see above).
            if (target == ParticipantLifecycleState.Active
                && p.Role == ParticipantRole.Speaker
                && !categorizedSpeakerIds.Contains(p.Id))
            {
                refusedIds.Add(p.Id);
                continue;
            }

            // §245 (operator 2026-07-07): SPEAKERS skip the Preselected state — their
            // pre-selection already happened in Sessionize, so the speaker queue
            // lifecycle is Inactive → Active in ONE step. A speaker caught in a
            // Preselect batch is left untouched (not counted as changed); volunteers /
            // media keep the 3-state flow.
            if (target == ParticipantLifecycleState.Preselected
                && p.Role == ParticipantRole.Speaker) continue;

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
        return new QueueResult(targets.Count, changed, activatedIds, refusedIds);
    }

    // Distinct + drop non-positive ids so a stray "0"/duplicate from a posted
    // form never widens the match set.
    private static List<int> Normalize(IEnumerable<int> ids) =>
        ids.Where(id => id > 0).Distinct().ToList();
}
