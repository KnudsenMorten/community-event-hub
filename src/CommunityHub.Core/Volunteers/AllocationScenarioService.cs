using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// The GENERIC stage → simulate → commit allocation workflow (REQUIREMENTS §129). An
/// organizer builds a named <see cref="AllocationScenario"/> of staged moves (by hand or
/// auto-seeded from a drop-out), SIMULATES it strictly read-only (coverage gaps / capacity
/// breaches / conflicts / a before-after diff / an explicit "what breaks"), then COMMITS it
/// atomically (every move applied to the live <see cref="VolunteerTaskAssignment"/> table in
/// ONE transaction + an audit stamp) or DISCARDS it (no effect on live tables).
///
/// <para>It sits ON TOP of the existing volunteer/organizer task model — no parallel stack —
/// and reuses the same red/green coverage math as <see cref="OrganizerAllocationService"/> /
/// <see cref="VolunteerAllocationService"/> (assigned vs <see cref="VolunteerTask.ResourcesNeeded"/>).
/// Phase 1 covers the <see cref="AllocationScenarioKind.VolunteerAllocation"/> and
/// <see cref="AllocationScenarioKind.DropOutBackfill"/> kinds; hotel allocation is the modelled
/// phase-2 extension (<see cref="AllocationScenarioMove.HotelId"/> already exists).</para>
///
/// SILENT until commit: nothing here emails. Simulation never writes.
/// </summary>
public sealed class AllocationScenarioService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public AllocationScenarioService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private static void RequireOrganizer(VolunteerStructureService.ActorContext actor)
    {
        if (actor.Role != ParticipantRole.Organizer)
            throw new VolunteerAccessDeniedException("Organizer role required for allocation scenarios.");
    }

    // =====================================================================
    //  Build a scenario + stage moves (REQUIREMENTS §129.1)
    // =====================================================================

    /// <summary>Create an empty scenario owned by the requesting organizer. Hotel allocation
    /// is a later phase — only the task-allocation kinds are accepted today.</summary>
    public async Task<int> CreateAsync(
        VolunteerStructureService.ActorContext actor, AllocationScenarioKind kind,
        string title, string? notes = null, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        if (kind == AllocationScenarioKind.HotelAllocation)
            throw new VolunteerValidationException(
                "Hotel allocation scenarios are a later phase; use the Hotels / Room blocks pages for now.");

        var scenario = new AllocationScenario
        {
            EventId = actor.EventId,
            OwnerParticipantId = actor.ParticipantId,
            Kind = kind,
            Status = AllocationScenarioStatus.Draft,
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled scenario" : title.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.AllocationScenarios.Add(scenario);
        await _db.SaveChangesAsync(ct);
        return scenario.Id;
    }

    /// <summary>Stage an ASSIGN move (person → task) into a DRAFT scenario. Idempotent per
    /// (scenario, person, task). The target role decides which coverage queue it counts toward.</summary>
    public async Task<bool> AddTaskMoveAsync(
        VolunteerStructureService.ActorContext actor, int scenarioId, int participantId, int taskId,
        ParticipantRole targetRole = ParticipantRole.Volunteer, AllocationMoveOp op = AllocationMoveOp.Assign,
        CancellationToken ct = default)
    {
        var scenario = await DraftAsync(actor, scenarioId, ct);

        if (!await _db.VolunteerTasks.AnyAsync(t => t.Id == taskId && t.EventId == actor.EventId, ct))
            throw new VolunteerValidationException("Task not found in this edition.");
        if (!await _db.Participants.AnyAsync(p => p.Id == participantId && p.EventId == actor.EventId, ct))
            throw new VolunteerValidationException("Person not found in this edition.");

        var exists = await _db.AllocationScenarioMoves.AnyAsync(
            m => m.ScenarioId == scenario.Id && m.ParticipantId == participantId && m.TaskId == taskId, ct);
        if (exists) return true; // idempotent

        _db.AllocationScenarioMoves.Add(new AllocationScenarioMove
        {
            ScenarioId = scenario.Id,
            EventId = actor.EventId,
            ParticipantId = participantId,
            TaskId = taskId,
            Op = op,
            TargetRole = targetRole,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Remove one staged move from a draft scenario.</summary>
    public async Task<bool> RemoveMoveAsync(
        VolunteerStructureService.ActorContext actor, int scenarioId, int moveId, CancellationToken ct = default)
    {
        var scenario = await DraftAsync(actor, scenarioId, ct);
        var move = await _db.AllocationScenarioMoves.FirstOrDefaultAsync(
            m => m.Id == moveId && m.ScenarioId == scenario.Id, ct);
        if (move is null) return false;
        _db.AllocationScenarioMoves.Remove(move);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // =====================================================================
    //  Drop-out re-planning (REQUIREMENTS §129.2)
    // =====================================================================

    /// <summary>
    /// Auto-seed a <see cref="AllocationScenarioKind.DropOutBackfill"/> scenario from a person
    /// who has DROPPED: one UNASSIGN move per task they currently cover, so the organizer starts
    /// from "here is exactly what their drop leaves uncovered" rather than a blank page. The
    /// scenario is a DRAFT — nothing is unassigned until it is committed. Returns the new
    /// scenario id (or null if the person covers nothing, so there is nothing to backfill).
    /// </summary>
    public async Task<int?> SeedDropOutBackfillAsync(
        VolunteerStructureService.ActorContext actor, int droppedParticipantId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        var dropped = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == droppedParticipantId && p.EventId == actor.EventId, ct);
        if (dropped is null) throw new VolunteerValidationException("Person not found in this edition.");

        var covered = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == actor.EventId && a.ParticipantId == droppedParticipantId)
            .Select(a => a.TaskId).ToListAsync(ct);
        if (covered.Count == 0) return null; // nothing to backfill

        var now = _clock.GetUtcNow();
        var scenario = new AllocationScenario
        {
            EventId = actor.EventId,
            OwnerParticipantId = actor.ParticipantId,
            Kind = AllocationScenarioKind.DropOutBackfill,
            Status = AllocationScenarioStatus.Draft,
            Title = $"Backfill: {dropped.FullName} dropped out",
            Notes = $"Auto-seeded {now:yyyy-MM-dd HH:mm} — {covered.Count} task(s) to re-cover.",
            DroppedParticipantId = droppedParticipantId,
            CreatedAt = now,
        };
        _db.AllocationScenarios.Add(scenario);
        await _db.SaveChangesAsync(ct);

        // Seed the gaps the drop leaves: an UNASSIGN move for each task they cover. The
        // organizer then stages the replacement ASSIGN moves before committing.
        var targetRole = dropped.Role == ParticipantRole.Organizer
            ? ParticipantRole.Organizer : ParticipantRole.Volunteer;
        foreach (var taskId in covered)
        {
            _db.AllocationScenarioMoves.Add(new AllocationScenarioMove
            {
                ScenarioId = scenario.Id,
                EventId = actor.EventId,
                ParticipantId = droppedParticipantId,
                TaskId = taskId,
                Op = AllocationMoveOp.Unassign,
                TargetRole = targetRole,
                CreatedAt = now,
            });
        }
        await _db.SaveChangesAsync(ct);
        return scenario.Id;
    }

    // =====================================================================
    //  Simulate — strictly READ-ONLY, no writes (REQUIREMENTS §129.3)
    // =====================================================================

    public sealed record TaskDelta(
        int TaskId, string Title, int ResourcesNeeded,
        int LiveAssigned, int Added, int Removed, int Projected)
    {
        public int Shortfall => System.Math.Max(0, ResourcesNeeded - Projected);   // under-staffed
        public int Overage => System.Math.Max(0, Projected - ResourcesNeeded);     // over capacity
        public bool IsGap => Projected < ResourcesNeeded;
        public bool IsBreach => Projected > ResourcesNeeded;
    }

    public sealed record Conflict(int ParticipantId, string PersonName, string Detail);

    public sealed record SimulationResult(
        int ScenarioId, AllocationScenarioKind Kind, int MoveCount,
        IReadOnlyList<TaskDelta> TaskDeltas,
        IReadOnlyList<Conflict> Conflicts)
    {
        public IReadOnlyList<TaskDelta> Gaps => TaskDeltas.Where(d => d.IsGap).ToList();
        public IReadOnlyList<TaskDelta> Breaches => TaskDeltas.Where(d => d.IsBreach).ToList();
        public bool IsEmpty => MoveCount == 0;
        /// <summary>The "what breaks" gate: any under-staffed task, over-capacity task, or
        /// double-booking conflict the organizer should resolve before committing.</summary>
        public bool HasProblems => Gaps.Count > 0 || Breaches.Count > 0 || Conflicts.Count > 0;
    }

    /// <summary>
    /// Compute the scenario's effect WITHOUT writing anything (REQUIREMENTS §129.3): per-task
    /// before/after coverage (live assigned + staged adds − staged removes vs ResourcesNeeded),
    /// the resulting gaps + capacity breaches, and double-booking conflicts (a person who would
    /// end up on ≥2 tasks sharing the same due date). Produces only a result object.
    /// </summary>
    public async Task<SimulationResult> SimulateAsync(
        VolunteerStructureService.ActorContext actor, int scenarioId, CancellationToken ct = default)
    {
        var scenario = await OwnedAsync(actor, scenarioId, ct);
        var moves = await _db.AllocationScenarioMoves
            .Where(m => m.ScenarioId == scenario.Id).ToListAsync(ct);

        var taskIds = moves.Where(m => m.TaskId is int).Select(m => m.TaskId!.Value).Distinct().ToList();
        var tasks = await _db.VolunteerTasks
            .Where(t => taskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.ResourcesNeeded, t.DueDate })
            .ToListAsync(ct);

        // Live assignment counts per touched task, and the live (task→people) map for conflicts.
        var liveAssignments = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == actor.EventId && taskIds.Contains(a.TaskId))
            .Select(a => new { a.TaskId, a.ParticipantId })
            .ToListAsync(ct);

        var deltas = new List<TaskDelta>();
        foreach (var t in tasks)
        {
            var live = liveAssignments.Count(a => a.TaskId == t.Id);
            var added = moves.Count(m => m.TaskId == t.Id && m.Op == AllocationMoveOp.Assign);
            var removed = moves.Count(m => m.TaskId == t.Id && m.Op == AllocationMoveOp.Unassign);
            var projected = System.Math.Max(0, live + added - removed);
            deltas.Add(new TaskDelta(t.Id, t.Title, t.ResourcesNeeded, live, added, removed, projected));
        }
        deltas = deltas.OrderBy(d => d.Title).ToList();

        // Conflicts: project each person's resulting task set (live ± staged) and flag anyone
        // who would hold ≥2 tasks that share a non-null due date (a likely double-booking).
        var dueByTask = tasks.ToDictionary(t => t.Id, t => t.DueDate);
        var people = moves.Select(m => m.ParticipantId)
            .Concat(liveAssignments.Select(a => a.ParticipantId)).Distinct().ToList();
        var names = await _db.Participants
            .Where(p => people.Contains(p.Id))
            .Select(p => new { p.Id, p.FullName }).ToDictionaryAsync(x => x.Id, x => x.FullName, ct);

        var conflicts = new List<Conflict>();
        foreach (var pid in people)
        {
            var projectedTasks = liveAssignments.Where(a => a.ParticipantId == pid).Select(a => a.TaskId).ToHashSet();
            foreach (var m in moves.Where(m => m.ParticipantId == pid && m.TaskId is int))
            {
                if (m.Op == AllocationMoveOp.Assign) projectedTasks.Add(m.TaskId!.Value);
                else projectedTasks.Remove(m.TaskId!.Value);
            }
            // Only tasks in this scenario carry a known due date; group those by date.
            var dated = projectedTasks
                .Where(tid => dueByTask.TryGetValue(tid, out var d) && d is not null)
                .GroupBy(tid => dueByTask[tid]!.Value)
                .Where(g => g.Count() > 1)
                .ToList();
            foreach (var g in dated)
            {
                conflicts.Add(new Conflict(pid, names.GetValueOrDefault(pid, $"#{pid}"),
                    $"on {g.Count()} tasks on {g.Key:yyyy-MM-dd} (possible double-booking)"));
            }
        }

        return new SimulationResult(scenario.Id, scenario.Kind, moves.Count, deltas, conflicts);
    }

    // =====================================================================
    //  Commit — atomic, with an audit trail (REQUIREMENTS §129.4)
    // =====================================================================

    public sealed record CommitOutcome(int Assigned, int Unassigned, int Skipped);

    /// <summary>
    /// Apply every staged move to the LIVE <see cref="VolunteerTaskAssignment"/> table in one
    /// atomic unit and stamp the scenario Committed (the audit trail). Refuses an EMPTY scenario,
    /// and refuses one whose simulation shows an OVER-CAPACITY breach unless
    /// <paramref name="acknowledgeOverCapacity"/> is set (REQUIREMENTS §129.6). Committed == the
    /// persisted truth — there is no separate save.
    /// </summary>
    public async Task<CommitOutcome> CommitAsync(
        VolunteerStructureService.ActorContext actor, int scenarioId,
        bool acknowledgeOverCapacity = false, CancellationToken ct = default)
    {
        var scenario = await DraftAsync(actor, scenarioId, ct);
        var moves = await _db.AllocationScenarioMoves.Where(m => m.ScenarioId == scenario.Id).ToListAsync(ct);
        if (moves.Count == 0)
            throw new VolunteerValidationException("Nothing to commit — this scenario has no staged moves.");

        var sim = await SimulateAsync(actor, scenarioId, ct);
        if (sim.Breaches.Count > 0 && !acknowledgeOverCapacity)
            throw new VolunteerValidationException(
                $"This would over-fill {sim.Breaches.Count} task(s) beyond their needed resources. "
                + "Re-check the plan, or commit again acknowledging the over-capacity.");

        var now = _clock.GetUtcNow();
        var strategy = _db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(ct) : null;

            int assigned = 0, unassigned = 0, skipped = 0;
            foreach (var m in moves.Where(m => m.TaskId is int))
            {
                var taskId = m.TaskId!.Value;
                var existing = await _db.VolunteerTaskAssignments.FirstOrDefaultAsync(
                    a => a.EventId == actor.EventId && a.TaskId == taskId && a.ParticipantId == m.ParticipantId, ct);

                if (m.Op == AllocationMoveOp.Assign)
                {
                    if (existing is not null) { skipped++; continue; }
                    _db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
                    {
                        EventId = actor.EventId, TaskId = taskId, ParticipantId = m.ParticipantId,
                        AssignedByEmail = actor.Email, CreatedAt = now,
                    });
                    assigned++;
                }
                else // Unassign
                {
                    if (existing is null) { skipped++; continue; }
                    _db.VolunteerTaskAssignments.Remove(existing);
                    unassigned++;
                }
            }

            scenario.Status = AllocationScenarioStatus.Committed;
            scenario.CommittedAt = now;
            scenario.CommittedByEmail = actor.Email;
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return new CommitOutcome(assigned, unassigned, skipped);
        });
        return outcome;
    }

    /// <summary>Discard a draft scenario — mark it Discarded; no effect on the live tables.</summary>
    public async Task DiscardAsync(
        VolunteerStructureService.ActorContext actor, int scenarioId, CancellationToken ct = default)
    {
        var scenario = await DraftAsync(actor, scenarioId, ct);
        scenario.Status = AllocationScenarioStatus.Discarded;
        scenario.DiscardedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    // =====================================================================
    //  Reads
    // =====================================================================

    public async Task<List<AllocationScenario>> ListAsync(
        VolunteerStructureService.ActorContext actor, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        return await _db.AllocationScenarios
            .Where(s => s.EventId == actor.EventId && s.OwnerParticipantId == actor.ParticipantId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<AllocationScenario?> GetWithMovesAsync(
        VolunteerStructureService.ActorContext actor, int scenarioId, CancellationToken ct = default)
    {
        RequireOrganizer(actor);
        return await _db.AllocationScenarios
            .Include(s => s.Moves).ThenInclude(m => m.Participant)
            .Include(s => s.Moves).ThenInclude(m => m.Task)
            .FirstOrDefaultAsync(
                s => s.Id == scenarioId && s.EventId == actor.EventId
                     && s.OwnerParticipantId == actor.ParticipantId, ct);
    }

    // --- guards -------------------------------------------------------------

    private async Task<AllocationScenario> OwnedAsync(
        VolunteerStructureService.ActorContext actor, int scenarioId, CancellationToken ct)
    {
        RequireOrganizer(actor);
        var s = await _db.AllocationScenarios.FirstOrDefaultAsync(
            x => x.Id == scenarioId && x.EventId == actor.EventId
                 && x.OwnerParticipantId == actor.ParticipantId, ct);
        return s ?? throw new VolunteerValidationException("Scenario not found.");
    }

    private async Task<AllocationScenario> DraftAsync(
        VolunteerStructureService.ActorContext actor, int scenarioId, CancellationToken ct)
    {
        var s = await OwnedAsync(actor, scenarioId, ct);
        if (s.Status != AllocationScenarioStatus.Draft)
            throw new VolunteerValidationException(
                $"This scenario is {s.Status} and can no longer be changed. Create a new scenario.");
        return s;
    }
}
