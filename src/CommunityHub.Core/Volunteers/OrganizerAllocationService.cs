using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// The ORGANIZER-side allocation queue (§150 route-by-Responsible-Team): the exact
/// mirror of <see cref="VolunteerAllocationService"/>, but it allocates ORGANIZERS
/// (teams <c>ELDK</c> / <c>ELDK-MOK</c>) instead of volunteers. It runs the same
/// 3-stage lifecycle — engine proposes (stage 1), the lead/organizer queues + edits
/// (stage 2 = <see cref="TaskAllocationDraft"/> rows), an organizer commits (stage 3
/// = real <see cref="VolunteerTaskAssignment"/> rows).
///
/// It deliberately reuses the SAME draft + assignment tables as the volunteer queue;
/// the two are kept apart purely by <see cref="TaskAllocationDraft.TargetRole"/>:
/// this service reads and writes ONLY <see cref="ParticipantRole.Organizer"/> drafts,
/// and <see cref="VolunteerAllocationService"/> reads and writes ONLY
/// <see cref="ParticipantRole.Volunteer"/> drafts, so an organizer's queue never
/// leaks into volunteer coverage (and vice-versa).
///
/// Like the volunteer side it is organizer-only for every WRITE; and additionally it
/// validates that the TARGET participant being queued/committed is itself an organizer.
/// The commit is RING-SCOPED through a distinct <c>organizer-allocation</c> Queue
/// feature key (§23a category 3): out-of-ring targets stay queued-but-dormant.
///
/// SILENT QUEUE (§150): nothing here emails — proposals and queue edits are mail-free;
/// only the commit path reports its <see cref="CommitResult.AffectedParticipantIds"/>,
/// which the batched commit-notifier (unit D) turns into one summary per person.
/// </summary>
public sealed class OrganizerAllocationService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly FeatureGateService _gate;
    private readonly RingResolver _rings;

    /// <summary>The role this service allocates (mirrors the volunteer one over a target role).</summary>
    private const ParticipantRole Target = ParticipantRole.Organizer;

    /// <summary>The QUEUE feature key whose released ring scopes a commit (category 3).</summary>
    public const string FeatureKey = "organizer-allocation";

    public OrganizerAllocationService(
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
    //  Gap detection (read) — counts ONLY this organizer queue's drafts.
    // =====================================================================

    /// <summary>
    /// Coverage for every task in the edition, including the requesting organizer's
    /// own ORGANIZER draft queue in the simulated count. Volunteer-targeted drafts are
    /// never counted here (TargetRole isolation).
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
                        && d.TargetRole == Target)
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
                             && d.TargetRole == Target, ct);

        return new TaskCoverage(task.Id, task.Title, task.ResourcesNeeded, assigned, draft);
    }

    // =====================================================================
    //  Draft queue (organizer-only; organizer TARGETS only).
    // =====================================================================

    /// <summary>Queue an ORGANIZER onto a task in the requesting organizer's DRAFT
    /// (a simulation — nothing is assigned). Idempotent per (task, organizer); an
    /// organizer already really assigned to the task is not queued again. The target
    /// MUST be an organizer (volunteers go through <see cref="VolunteerAllocationService"/>).</summary>
    public async Task<bool> AddDraftAsync(
        VolunteerStructureService.ActorContext actor, int taskId, int organizerId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        var task = await _db.VolunteerTasks.AnyAsync(
            t => t.Id == taskId && t.EventId == actor.EventId, ct);
        if (!task) throw new VolunteerValidationException("Task not found in this edition.");

        var target = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == organizerId && p.EventId == actor.EventId, ct);
        if (target is null) throw new VolunteerValidationException("Organizer not found in this edition.");
        if (target.Role != Target)
            throw new VolunteerValidationException("Only organizers can be allocated through the organizer queue.");

        // Already a real assignment ⇒ nothing to draft.
        var alreadyAssigned = await _db.VolunteerTaskAssignments.AnyAsync(
            a => a.TaskId == taskId && a.ParticipantId == organizerId, ct);
        if (alreadyAssigned) return false;

        var alreadyDrafted = await _db.TaskAllocationDrafts.AnyAsync(
            d => d.TaskId == taskId && d.ParticipantId == organizerId
                 && d.OwnerParticipantId == actor.ParticipantId
                 && d.TargetRole == Target, ct);
        if (alreadyDrafted) return true; // idempotent

        _db.TaskAllocationDrafts.Add(new TaskAllocationDraft
        {
            EventId = actor.EventId,
            OwnerParticipantId = actor.ParticipantId,
            TaskId = taskId,
            ParticipantId = organizerId,
            TargetRole = Target,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Remove one queued allocation from the organizer's draft.</summary>
    public async Task<bool> RemoveDraftAsync(
        VolunteerStructureService.ActorContext actor, int taskId, int organizerId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var row = await _db.TaskAllocationDrafts.FirstOrDefaultAsync(
            d => d.TaskId == taskId && d.ParticipantId == organizerId
                 && d.OwnerParticipantId == actor.ParticipantId && d.EventId == actor.EventId
                 && d.TargetRole == Target, ct);
        if (row is null) return false;
        _db.TaskAllocationDrafts.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The organizer's current ORGANIZER draft queue (for showing the pending list).</summary>
    public async Task<List<TaskAllocationDraft>> LoadDraftAsync(
        VolunteerStructureService.ActorContext actor, CancellationToken ct = default) =>
        await _db.TaskAllocationDrafts
            .Where(d => d.EventId == actor.EventId && d.OwnerParticipantId == actor.ParticipantId
                        && d.TargetRole == Target)
            .Include(d => d.Participant)
            .Include(d => d.Task)
            .OrderBy(d => d.Task.Title)
            .ToListAsync(ct);

    /// <summary>
    /// COMMIT the organizer draft queue: turn every queued allocation into a real
    /// <see cref="VolunteerTaskAssignment"/>, then clear the consumed drafts. Re-runs of
    /// the same person on a task that is now assigned are skipped (counted separately).
    /// RING-SCOPED via the <see cref="FeatureKey"/> feature (out-of-ring targets stay
    /// queued-but-dormant). This is the ONLY method that creates real assignments from
    /// the organizer drafts; it is also the ONLY place that reports affected people for
    /// the §150 batched commit notifier (no email is sent here).
    /// </summary>
    public async Task<CommitResult> CommitAsync(
        VolunteerStructureService.ActorContext actor, CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        var drafts = await _db.TaskAllocationDrafts
            .Where(d => d.EventId == actor.EventId && d.OwnerParticipantId == actor.ParticipantId
                        && d.TargetRole == Target)
            .ToListAsync(ct);
        if (drafts.Count == 0) return new CommitResult(0, 0);

        // RING-SCOPED COMMIT (§23a, category 3 Queue): the released ring of the
        // organizer-allocation feature scopes the impact. A draft whose TARGET
        // organizer's effective ring is above that ring is LEFT in the queue
        // (committed-but-dormant). Default released ring is Broad (GA), so by default
        // every target is in scope.
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

    /// <summary>DISCARD the organizer's whole ORGANIZER draft queue — nothing is assigned,
    /// and volunteer drafts are untouched.</summary>
    public async Task<int> DiscardAsync(
        VolunteerStructureService.ActorContext actor, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        var drafts = await _db.TaskAllocationDrafts
            .Where(d => d.EventId == actor.EventId && d.OwnerParticipantId == actor.ParticipantId
                        && d.TargetRole == Target)
            .ToListAsync(ct);
        if (drafts.Count == 0) return 0;
        _db.TaskAllocationDrafts.RemoveRange(drafts);
        await _db.SaveChangesAsync(ct);
        return drafts.Count;
    }
}
