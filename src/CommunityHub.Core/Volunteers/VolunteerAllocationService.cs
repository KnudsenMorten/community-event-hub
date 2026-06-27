using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
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

/// <summary>The outcome of committing a draft queue into real assignments.
/// <paramref name="SkippedOutOfRing"/> drafts target a volunteer whose ring is above
/// the feature's released ring — they are LEFT in the queue (committed-but-dormant),
/// so promoting the ring in /Organizer/Settings and re-committing picks them up.
/// <paramref name="AffectedParticipantIds"/> is the DISTINCT set of target participant
/// ids whose committed assignments changed this commit (a NEW real assignment was
/// created) — the ONLY input the §150 batched commit-notifier (unit D) needs to mail
/// one summary per affected person. Empty when nothing changed; never the proposals or
/// the lead's queue edits (those are mail-free).</summary>
public sealed record CommitResult(int Committed, int SkippedDuplicate, int SkippedOutOfRing = 0)
{
    /// <summary>Distinct committed targets (never null — empty when none changed).</summary>
    public IReadOnlyList<int> AffectedParticipantIds { get; init; } = Array.Empty<int>();
}

/// <summary>
/// The outcome of seeding a drop-out re-plan: how many of the dropped volunteer's
/// real assignments were freed, how many distinct tasks that affected, and how many
/// backfill candidates were queued as DRAFTS for the organizer to review + commit.
/// </summary>
public sealed record DropoutReplanResult(int FreedAssignments, int AffectedTasks, int SeededBackfillDrafts);

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
    private readonly FeatureGateService _gate;
    private readonly RingResolver _rings;

    /// <summary>The QUEUE feature key whose released ring scopes a commit (category 3).</summary>
    public const string FeatureKey = "volunteer-allocation";

    public VolunteerAllocationService(
        CommunityHubDbContext db, TimeProvider clock,
        FeatureGateService gate, RingResolver rings)
    {
        _db = db;
        _clock = clock;
        _gate = gate;
        _rings = rings;
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
            .Where(d => d.EventId == actor.EventId && d.OwnerParticipantId == actor.ParticipantId
                        && d.TargetRole == ParticipantRole.Volunteer)
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
                             && d.OwnerParticipantId == actor.ParticipantId
                             && d.TargetRole == ParticipantRole.Volunteer, ct);

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
                 && d.OwnerParticipantId == actor.ParticipantId
                 && d.TargetRole == ParticipantRole.Volunteer, ct);
        if (alreadyDrafted) return true; // idempotent

        _db.TaskAllocationDrafts.Add(new TaskAllocationDraft
        {
            EventId = actor.EventId,
            OwnerParticipantId = actor.ParticipantId,
            TaskId = taskId,
            ParticipantId = volunteerId,
            TargetRole = ParticipantRole.Volunteer,
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
                 && d.OwnerParticipantId == actor.ParticipantId && d.EventId == actor.EventId
                 && d.TargetRole == ParticipantRole.Volunteer, ct);
        if (row is null) return false;
        _db.TaskAllocationDrafts.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The organizer's current draft queue (for showing the pending list).</summary>
    public async Task<List<TaskAllocationDraft>> LoadDraftAsync(
        VolunteerStructureService.ActorContext actor, CancellationToken ct = default) =>
        await _db.TaskAllocationDrafts
            .Where(d => d.EventId == actor.EventId && d.OwnerParticipantId == actor.ParticipantId
                        && d.TargetRole == ParticipantRole.Volunteer)
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
            .Where(d => d.EventId == actor.EventId && d.OwnerParticipantId == actor.ParticipantId
                        && d.TargetRole == ParticipantRole.Volunteer)
            .ToListAsync(ct);
        if (drafts.Count == 0) return new CommitResult(0, 0);

        // RING-SCOPED COMMIT (REQUIREMENTS §23a, category 3 Queue): the released ring of
        // the volunteer-allocation feature scopes the impact. A draft whose TARGET
        // volunteer's effective ring is above that ring is LEFT in the queue
        // (committed-but-dormant) — so even with 40 staged, only in-ring volunteers get a
        // real assignment now; promote the ring + re-commit to widen. Default released
        // ring is Broad (GA), so by default every target is in scope (no behaviour change).
        var releasedRing = await _gate.GetReleasedRingAsync(FeatureKey, actor.EventId, ct);

        int committed = 0, skipped = 0, outOfRing = 0;
        var now = _clock.GetUtcNow();
        var consumed = new List<TaskAllocationDraft>();
        var affected = new HashSet<int>();   // distinct targets whose committed set changed
        foreach (var d in drafts)
        {
            var targetRing = await _rings.GetEffectiveRingAsync(d.ParticipantId, ct);
            if (!Rings.IsActiveForRing(targetRing, releasedRing))
            {
                outOfRing++;   // leave this draft in the queue (dormant until promoted)
                continue;
            }

            consumed.Add(d);   // this draft is resolved this commit -> removable

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
            affected.Add(d.ParticipantId);   // §150: this person needs a commit summary
        }

        // Consume only the in-ring drafts; out-of-ring drafts stay queued.
        _db.TaskAllocationDrafts.RemoveRange(consumed);
        await _db.SaveChangesAsync(ct);
        return new CommitResult(committed, skipped, outOfRing) { AffectedParticipantIds = affected.ToList() };
    }

    /// <summary>
    /// DROP-OUT RE-PLAN: a volunteer has pulled out. Free their real task
    /// assignments (they are gone), then SEED the organizer's draft queue with
    /// backfill candidates for each now-short task — available volunteers (those who
    /// submitted availability are preferred) who aren't already on the task. Nothing
    /// is re-assigned here: the organizer reviews the seeded drafts in the live
    /// coverage simulation and <see cref="CommitAsync"/>s (or <see cref="DiscardAsync"/>s)
    /// them. Freeing the dropped volunteer's slots IS applied immediately (they left).
    /// </summary>
    public async Task<DropoutReplanResult> SeedDropoutBackfillAsync(
        VolunteerStructureService.ActorContext actor, int droppedParticipantId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        // The dropped volunteer's real assignments in this edition.
        var theirs = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == actor.EventId && a.ParticipantId == droppedParticipantId)
            .ToListAsync(ct);
        if (theirs.Count == 0) return new DropoutReplanResult(0, 0, 0);

        var taskIds = theirs.Select(a => a.TaskId).Distinct().ToList();

        // They left — free their slots now.
        _db.VolunteerTaskAssignments.RemoveRange(theirs);
        await _db.SaveChangesAsync(ct);

        // Candidate pool: active volunteers in the edition (excluding the leaver),
        // those who submitted availability preferred so we propose people likely to
        // say yes first.
        var availableIds = new HashSet<int>(
            (await _db.VolunteerDayAvailabilities
                .Where(v => v.EventId == actor.EventId).Select(v => v.ParticipantId).ToListAsync(ct))
            .Concat(await _db.VolunteerAvailabilities
                .Where(v => v.EventId == actor.EventId).Select(v => v.ParticipantId).ToListAsync(ct)));

        var pool = (await _db.Participants
                .Where(p => p.EventId == actor.EventId
                            && p.Role == ParticipantRole.Volunteer
                            && p.IsActive
                            && p.Id != droppedParticipantId)
                .Select(p => p.Id)
                .ToListAsync(ct))
            .OrderByDescending(id => availableIds.Contains(id))
            .ThenBy(id => id)
            .ToList();

        var now = _clock.GetUtcNow();
        var seeded = 0;
        foreach (var taskId in taskIds)
        {
            var task = await _db.VolunteerTasks.FirstOrDefaultAsync(
                t => t.Id == taskId && t.EventId == actor.EventId, ct);
            if (task is null) continue;

            var assignedNow = await _db.VolunteerTaskAssignments
                .CountAsync(a => a.TaskId == taskId && a.EventId == actor.EventId, ct);
            var draftedNow = await _db.TaskAllocationDrafts
                .CountAsync(d => d.TaskId == taskId && d.EventId == actor.EventId
                                 && d.OwnerParticipantId == actor.ParticipantId
                                 && d.TargetRole == ParticipantRole.Volunteer, ct);
            var shortfall = Math.Max(0, task.ResourcesNeeded - assignedNow - draftedNow);
            if (shortfall == 0) continue;

            // On-task ids (real or already drafted) to avoid proposing duplicates.
            var onTask = new HashSet<int>(
                (await _db.VolunteerTaskAssignments
                    .Where(a => a.TaskId == taskId && a.EventId == actor.EventId)
                    .Select(a => a.ParticipantId).ToListAsync(ct))
                .Concat(await _db.TaskAllocationDrafts
                    .Where(d => d.TaskId == taskId && d.EventId == actor.EventId
                                && d.OwnerParticipantId == actor.ParticipantId
                                && d.TargetRole == ParticipantRole.Volunteer)
                    .Select(d => d.ParticipantId).ToListAsync(ct)));

            foreach (var candidateId in pool)
            {
                if (shortfall == 0) break;
                if (onTask.Contains(candidateId)) continue;
                _db.TaskAllocationDrafts.Add(new TaskAllocationDraft
                {
                    EventId = actor.EventId,
                    OwnerParticipantId = actor.ParticipantId,
                    TaskId = taskId,
                    ParticipantId = candidateId,
                    TargetRole = ParticipantRole.Volunteer,
                    CreatedAt = now,
                });
                onTask.Add(candidateId);
                seeded++;
                shortfall--;
            }
        }

        if (seeded > 0) await _db.SaveChangesAsync(ct);
        return new DropoutReplanResult(theirs.Count, taskIds.Count, seeded);
    }

    /// <summary>DISCARD the organizer's whole draft queue — nothing is assigned.</summary>
    public async Task<int> DiscardAsync(
        VolunteerStructureService.ActorContext actor, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var drafts = await _db.TaskAllocationDrafts
            .Where(d => d.EventId == actor.EventId && d.OwnerParticipantId == actor.ParticipantId
                        && d.TargetRole == ParticipantRole.Volunteer)
            .ToListAsync(ct);
        if (drafts.Count == 0) return 0;
        _db.TaskAllocationDrafts.RemoveRange(drafts);
        await _db.SaveChangesAsync(ct);
        return drafts.Count;
    }
}
