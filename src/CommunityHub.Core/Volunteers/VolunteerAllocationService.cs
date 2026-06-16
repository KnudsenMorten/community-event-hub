using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// The resource-coverage view of a single task: how many people it needs vs how
/// many are committed (real assignments) and queued (draft allocations). Drives the
/// red/green gap indicator.
/// </summary>
public sealed record TaskCoverage(
    int TaskId,
    string Title,
    int ResourcesNeeded,
    int AssignedCount,
    int DraftCount)
{
    /// <summary>Total people counted in the SIMULATION = committed + queued.</summary>
    public int SimulatedCount => AssignedCount + DraftCount;

    /// <summary>Shortfall using only REAL assignments (current reality).</summary>
    public int Shortfall => Math.Max(0, ResourcesNeeded - AssignedCount);

    /// <summary>Shortfall in the SIMULATION (after the draft is committed).</summary>
    public int SimulatedShortfall => Math.Max(0, ResourcesNeeded - SimulatedCount);

    /// <summary>GREEN when the simulation covers the need (needed &lt;= committed+queued);
    /// RED when still short. ResourcesNeeded == 0 is always green (no requirement).</summary>
    public bool IsCovered => ResourcesNeeded <= SimulatedCount;
}

/// <summary>The outcome of committing a draft queue into real assignments.</summary>
public sealed record CommitResult(int Committed, int SkippedDuplicate);

/// <summary>
/// The TASK-MAPPER engine: gap detection plus the DRAFT → COMMIT allocation queue.
///
/// The organizer queues people→tasks as <see cref="TaskAllocationDraft"/> rows and
/// sees the red/green coverage update LIVE as a SIMULATION (drafts count toward the
/// simulated total but are NOT real assignments). Only <see cref="CommitAsync"/>
/// turns the draft queue into real <see cref="VolunteerTaskAssignment"/> rows;
/// <see cref="DiscardAsync"/> throws the draft away without ever assigning anyone.
/// Drafts are scoped per organizer so concurrent planning sessions don't collide.
///
/// All writes are organizer-only (a plain volunteer can't allocate others).
/// </summary>
public sealed class VolunteerAllocationService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public VolunteerAllocationService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private static void RequireOrganizer(VolunteerStructureService.ActorContext actor)
    {
        if (actor.Role != ParticipantRole.Organizer)
            throw new VolunteerAccessDeniedException("Organizer role required for allocation.");
    }

    // =====================================================================
    //  Gap detection (read).
    // =====================================================================

    /// <summary>
    /// Coverage for every task in the edition, including the requesting organizer's
    /// own draft queue in the simulated count. A plain volunteer may also read this
    /// (drafts then count 0 for them) so a supervisor dashboard can show gaps.
    /// </summary>
    public async Task<List<TaskCoverage>> LoadCoverageAsync(
        VolunteerStructureService.ActorContext actor, CancellationToken ct = default)
    {
        var tasks = await _db.VolunteerTasks
            .Where(t => t.EventId == actor.EventId)
            .Select(t => new { t.Id, t.Title, t.ResourcesNeeded })
            .ToListAsync(ct);

        var assignedCounts = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == actor.EventId)
            .GroupBy(a => a.TaskId)
            .Select(g => new { TaskId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TaskId, x => x.Count, ct);

        var draftCounts = await _db.TaskAllocationDrafts
            .Where(d => d.EventId == actor.EventId && d.OwnerParticipantId == actor.ParticipantId)
            .GroupBy(d => d.TaskId)
            .Select(g => new { TaskId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TaskId, x => x.Count, ct);

        return tasks
            .Select(t => new TaskCoverage(
                t.Id, t.Title, t.ResourcesNeeded,
                assignedCounts.GetValueOrDefault(t.Id, 0),
                draftCounts.GetValueOrDefault(t.Id, 0)))
            .OrderBy(c => c.Title)
            .ToList();
    }

    /// <summary>Coverage for one task (live simulation for the requesting organizer).</summary>
    public async Task<TaskCoverage?> LoadTaskCoverageAsync(
        VolunteerStructureService.ActorContext actor, int taskId, CancellationToken ct = default)
    {
        var task = await _db.VolunteerTasks.FirstOrDefaultAsync(
            t => t.Id == taskId && t.EventId == actor.EventId, ct);
        if (task is null) return null;

        var assigned = await _db.VolunteerTaskAssignments
            .CountAsync(a => a.TaskId == taskId && a.EventId == actor.EventId, ct);
        var draft = await _db.TaskAllocationDrafts
            .CountAsync(d => d.TaskId == taskId && d.EventId == actor.EventId
                             && d.OwnerParticipantId == actor.ParticipantId, ct);

        return new TaskCoverage(task.Id, task.Title, task.ResourcesNeeded, assigned, draft);
    }

    // =====================================================================
    //  Draft queue (organizer-only).
    // =====================================================================

    /// <summary>Queue a volunteer onto a task in the organizer's DRAFT (a
    /// simulation — nothing is assigned). Idempotent per (task, volunteer); a
    /// volunteer already really assigned to the task is not queued again.</summary>
    public async Task<bool> AddDraftAsync(
        VolunteerStructureService.ActorContext actor, int taskId, int volunteerId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        var task = await _db.VolunteerTasks.AnyAsync(
            t => t.Id == taskId && t.EventId == actor.EventId, ct);
        if (!task) throw new VolunteerValidationException("Task not found in this edition.");

        var vol = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == volunteerId && p.EventId == actor.EventId, ct);
        if (vol is null) throw new VolunteerValidationException("Volunteer not found in this edition.");
        if (vol.Role != ParticipantRole.Volunteer)
            throw new VolunteerValidationException("Only volunteers can be allocated to tasks.");

        // Already a real assignment ⇒ nothing to draft.
        var alreadyAssigned = await _db.VolunteerTaskAssignments.AnyAsync(
            a => a.TaskId == taskId && a.ParticipantId == volunteerId, ct);
        if (alreadyAssigned) return false;

        var alreadyDrafted = await _db.TaskAllocationDrafts.AnyAsync(
            d => d.TaskId == taskId && d.ParticipantId == volunteerId
                 && d.OwnerParticipantId == actor.ParticipantId, ct);
        if (alreadyDrafted) return true; // idempotent

        _db.TaskAllocationDrafts.Add(new TaskAllocationDraft
        {
            EventId = actor.EventId,
            OwnerParticipantId = actor.ParticipantId,
            TaskId = taskId,
            ParticipantId = volunteerId,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Remove one queued allocation from the organizer's draft.</summary>
    public async Task<bool> RemoveDraftAsync(
        VolunteerStructureService.ActorContext actor, int taskId, int volunteerId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var row = await _db.TaskAllocationDrafts.FirstOrDefaultAsync(
            d => d.TaskId == taskId && d.ParticipantId == volunteerId
                 && d.OwnerParticipantId == actor.ParticipantId && d.EventId == actor.EventId, ct);
        if (row is null) return false;
        _db.TaskAllocationDrafts.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The organizer's current draft queue (for showing the pending list).</summary>
    public async Task<List<TaskAllocationDraft>> LoadDraftAsync(
        VolunteerStructureService.ActorContext actor, CancellationToken ct = default) =>
        await _db.TaskAllocationDrafts
            .Where(d => d.EventId == actor.EventId && d.OwnerParticipantId == actor.ParticipantId)
            .Include(d => d.Participant)
            .Include(d => d.Task)
            .OrderBy(d => d.Task.Title)
            .ToListAsync(ct);

    /// <summary>
    /// COMMIT the draft queue: turn every queued allocation into a real
    /// <see cref="VolunteerTaskAssignment"/>, then clear the queue. Re-runs of the
    /// same person on a task that is now assigned are skipped (counted separately).
    /// This is the ONLY method that creates real assignments from drafts.
    /// </summary>
    public async Task<CommitResult> CommitAsync(
        VolunteerStructureService.ActorContext actor, CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        var drafts = await _db.TaskAllocationDrafts
            .Where(d => d.EventId == actor.EventId && d.OwnerParticipantId == actor.ParticipantId)
            .ToListAsync(ct);
        if (drafts.Count == 0) return new CommitResult(0, 0);

        int committed = 0, skipped = 0;
        var now = _clock.GetUtcNow();
        foreach (var d in drafts)
        {
            var exists = await _db.VolunteerTaskAssignments.AnyAsync(
                a => a.TaskId == d.TaskId && a.ParticipantId == d.ParticipantId, ct);
            if (exists) { skipped++; continue; }

            _db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
            {
                EventId = actor.EventId,
                TaskId = d.TaskId,
                ParticipantId = d.ParticipantId,
                AssignedByEmail = actor.Email,
                CreatedAt = now,
            });
            committed++;
        }

        _db.TaskAllocationDrafts.RemoveRange(drafts); // queue is consumed on commit
        await _db.SaveChangesAsync(ct);
        return new CommitResult(committed, skipped);
    }

    /// <summary>DISCARD the organizer's whole draft queue — nothing is assigned.</summary>
    public async Task<int> DiscardAsync(
        VolunteerStructureService.ActorContext actor, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var drafts = await _db.TaskAllocationDrafts
            .Where(d => d.EventId == actor.EventId && d.OwnerParticipantId == actor.ParticipantId)
            .ToListAsync(ct);
        if (drafts.Count == 0) return 0;
        _db.TaskAllocationDrafts.RemoveRange(drafts);
        await _db.SaveChangesAsync(ct);
        return drafts.Count;
    }
}
