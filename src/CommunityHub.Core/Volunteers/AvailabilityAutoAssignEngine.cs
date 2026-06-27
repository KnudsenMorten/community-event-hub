using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// One candidate considered by <see cref="AvailabilityAutoAssignEngine"/>: the
/// participant and their derived day-availability CAPACITY (the
/// <see cref="VolunteerDayAvailability"/> percentage, with <c>Unavailable</c>
/// clamped to 0). A candidate with no positive capacity (only Blocked/Unavailable,
/// or no availability submitted at all) is NOT eligible to be proposed.
/// </summary>
public sealed record AvailabilityCandidate(int ParticipantId, int Capacity);

/// <summary>
/// One auto-proposed allocation: <paramref name="ParticipantId"/> proposed onto
/// <paramref name="TaskId"/> for the <paramref name="TargetRole"/> queue. A PROPOSAL
/// only — it becomes a <see cref="TaskAllocationDraft"/> (stage-1 EngineProposed) when
/// seeded, and a real assignment only when the organizer commits the queue.
/// </summary>
public sealed record AllocationProposal(int TaskId, int ParticipantId, ParticipantRole TargetRole);

/// <summary>
/// §150 STEP-2: the availability AUTO-ASSIGN engine, GENERALIZED over a target
/// <see cref="ParticipantRole"/> (volunteer or organizer). It reads each candidate's
/// stored-but-unused <see cref="VolunteerDayAvailability"/> signal, clamps
/// <c>Unavailable</c> to zero capacity (Half = 50 / Full = 100), measures each task's
/// <c>ResourcesNeeded</c> against the people already assigned
/// (<see cref="VolunteerTaskAssignment"/>) plus already drafted
/// (<see cref="TaskAllocationDraft"/>), and PROPOSES candidates to cover the gap —
/// but ONLY for tasks the <see cref="ResponsibleTeamRouter"/> routes to that role.
/// Tracked-only tasks are skipped (imported, never auto-assigned/queued).
///
/// <para>Proposals are seeded as <see cref="DraftSource.EngineProposed"/> draft rows
/// (the SILENT queue): seeding NEVER sends email — the only email path is the
/// organizer COMMIT in <see cref="VolunteerAllocationService"/>. The candidate-pool
/// ordering (availability preferred, then by id) mirrors
/// <see cref="VolunteerAllocationService.SeedDropoutBackfillAsync"/>.</para>
/// </summary>
public sealed class AvailabilityAutoAssignEngine
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public AvailabilityAutoAssignEngine(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private static void RequireOrganizer(VolunteerStructureService.ActorContext actor)
    {
        if (actor.Role != ParticipantRole.Organizer)
            throw new VolunteerAccessDeniedException("Organizer role required for auto-assign.");
    }

    /// <summary>The queue kind that the given target role's proposals belong to, or
    /// null if the role has no auto-assign queue (only Volunteer/Organizer do).</summary>
    private static AllocationQueueKind? QueueKindFor(ParticipantRole targetRole) => targetRole switch
    {
        ParticipantRole.Volunteer => AllocationQueueKind.Volunteer,
        ParticipantRole.Organizer => AllocationQueueKind.Organizer,
        _ => null,
    };

    /// <summary>
    /// PURE proposal computation (no DB, no I/O) — the testable core. For every task
    /// the router routes to <paramref name="targetRole"/> and that is still short
    /// (<c>ResourcesNeeded</c> &gt; existing assigned + drafted), proposes the most
    /// available eligible candidates (Full before Half; Blocked/Unavailable excluded),
    /// up to the gap, skipping anyone already on the task. Tracked-only tasks are
    /// skipped entirely. Deterministic ordering: capacity descending, then id.
    /// </summary>
    /// <param name="tasks">Candidate tasks (any team; the router filters them).</param>
    /// <param name="candidates">The target-role pool with derived capacity.</param>
    /// <param name="existingCountByTask">TaskId → already assigned + already drafted (counts toward the budget).</param>
    /// <param name="occupied">(TaskId, ParticipantId) pairs already assigned OR drafted (never re-propose).</param>
    /// <param name="targetRole">The role this run proposes for.</param>
    public static IReadOnlyList<AllocationProposal> ComputeProposals(
        IEnumerable<VolunteerTask> tasks,
        IReadOnlyCollection<AvailabilityCandidate> candidates,
        IReadOnlyDictionary<int, int> existingCountByTask,
        ISet<(int TaskId, int ParticipantId)> occupied,
        ParticipantRole targetRole)
    {
        var wanted = QueueKindFor(targetRole);
        if (wanted is null) return Array.Empty<AllocationProposal>();

        // Availability preference: Full (100) before Half (50); Unavailable (clamped
        // to 0) and Blocked (0) are not eligible. Stable tie-break by id — same
        // ordering shape VolunteerAllocationService.SeedDropoutBackfillAsync uses.
        var ordered = candidates
            .Where(c => c.Capacity > 0)
            .OrderByDescending(c => c.Capacity)
            .ThenBy(c => c.ParticipantId)
            .ToList();

        var proposals = new List<AllocationProposal>();
        foreach (var task in tasks)
        {
            if (ResponsibleTeamRouter.Route(task.ResponsibleTeam) != wanted.Value)
                continue; // tracked-only / other-queue task — never auto-assigned here.

            var have = existingCountByTask.TryGetValue(task.Id, out var n) ? n : 0;
            var gap = task.ResourcesNeeded - have;
            if (gap <= 0) continue;

            foreach (var c in ordered)
            {
                if (gap == 0) break;
                if (occupied.Contains((task.Id, c.ParticipantId))) continue;
                proposals.Add(new AllocationProposal(task.Id, c.ParticipantId, targetRole));
                gap--;
            }
        }
        return proposals;
    }

    /// <summary>
    /// Load the edition's state, <see cref="ComputeProposals"/>, and PERSIST the
    /// result as <see cref="DraftSource.EngineProposed"/> <see cref="TaskAllocationDraft"/>
    /// rows for <paramref name="ownerParticipantId"/>'s queue with the matching
    /// <see cref="TaskAllocationDraft.TargetRole"/>. Idempotent per
    /// (TaskId, ParticipantId): a re-run proposes nothing already assigned or already
    /// drafted, so no duplicates. SILENT — sends NO email (the queue is mail-free;
    /// only the organizer commit notifies). Returns the number of drafts seeded.
    /// </summary>
    public async Task<int> SeedProposalsAsync(
        VolunteerStructureService.ActorContext actor,
        ParticipantRole targetRole,
        int ownerParticipantId,
        CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        if (QueueKindFor(targetRole) is null) return 0;

        var eventId = actor.EventId;

        var tasks = await _db.VolunteerTasks
            .Where(t => t.EventId == eventId)
            .ToListAsync(ct);
        if (tasks.Count == 0) return 0;

        // Candidate pool: active participants OF THE TARGET ROLE in the edition,
        // each carrying their derived day-availability capacity (max across days,
        // Unavailable clamped to 0). No availability => capacity 0 => not eligible.
        var poolIds = await _db.Participants
            .Where(p => p.EventId == eventId && p.Role == targetRole && p.IsActive)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var capacityByPid = (await _db.VolunteerDayAvailabilities
                .Where(v => v.EventId == eventId)
                .Select(v => new { v.ParticipantId, v.Level })
                .ToListAsync(ct))
            .GroupBy(x => x.ParticipantId)
            .ToDictionary(g => g.Key, g => g.Max(x => Math.Max(0, (int)x.Level)));

        var candidates = poolIds
            .Select(id => new AvailabilityCandidate(
                id, capacityByPid.TryGetValue(id, out var cap) ? cap : 0))
            .ToList();

        // Real assignments (any participant) and THIS owner's existing drafts both
        // count toward the budget and block re-proposing the same person on a task.
        var assignments = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == eventId)
            .Select(a => new { a.TaskId, a.ParticipantId })
            .ToListAsync(ct);

        var drafts = await _db.TaskAllocationDrafts
            .Where(d => d.EventId == eventId && d.OwnerParticipantId == ownerParticipantId)
            .Select(d => new { d.TaskId, d.ParticipantId })
            .ToListAsync(ct);

        var existingCountByTask = new Dictionary<int, int>();
        var occupied = new HashSet<(int TaskId, int ParticipantId)>();
        foreach (var a in assignments)
        {
            existingCountByTask[a.TaskId] = existingCountByTask.GetValueOrDefault(a.TaskId) + 1;
            occupied.Add((a.TaskId, a.ParticipantId));
        }
        foreach (var d in drafts)
        {
            existingCountByTask[d.TaskId] = existingCountByTask.GetValueOrDefault(d.TaskId) + 1;
            occupied.Add((d.TaskId, d.ParticipantId));
        }

        var proposals = ComputeProposals(tasks, candidates, existingCountByTask, occupied, targetRole);
        if (proposals.Count == 0) return 0;

        var now = _clock.GetUtcNow();
        foreach (var p in proposals)
        {
            _db.TaskAllocationDrafts.Add(new TaskAllocationDraft
            {
                EventId = eventId,
                OwnerParticipantId = ownerParticipantId,
                TaskId = p.TaskId,
                ParticipantId = p.ParticipantId,
                TargetRole = p.TargetRole,
                Source = DraftSource.EngineProposed,
                CreatedAt = now,
            });
        }

        // SILENT queue: persist only — no IEmailSender is involved at any point.
        await _db.SaveChangesAsync(ct);
        return proposals.Count;
    }
}
