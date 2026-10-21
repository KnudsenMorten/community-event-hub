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
        if (!actor.Role.HasOrganizerAccess())
            throw new VolunteerAccessDeniedException("Organizer role required for auto-assign.");
    }

    /// <summary>
    /// §1297 — the capacity-only proposal path, kept for tasks with NO scheduled day.
    /// Delegates to <see cref="VolunteerPlanner.Plan"/> with empty availability, which makes
    /// every candidate eligible for every undated task — the behaviour this method has always
    /// had. Scheduled tasks go through <see cref="SeedProposalsAsync"/>, which supplies real
    /// windows and gets interval matching.
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
        var plannerCandidates = candidates
            .Select(c => new PlannerCandidate(
                c.ParticipantId, c.Capacity,
                new Dictionary<DateOnly, DayWindow>(),
                Array.Empty<(DateOnly, TimeWindow)>(),
                0d))
            .ToList();

        return VolunteerPlanner.Plan(
            tasks.Select(t => PlannerTask.From(t)),
            plannerCandidates, existingCountByTask, occupied, targetRole,
            PlannerScope.Everyone).Proposals;
    }

    /// <summary>
    /// Load the edition's state, run <see cref="VolunteerPlanner.Plan"/>, and PERSIST the
    /// result as <see cref="DraftSource.EngineProposed"/> <see cref="TaskAllocationDraft"/>
    /// rows for <paramref name="ownerParticipantId"/>'s queue with the matching
    /// <see cref="TaskAllocationDraft.TargetRole"/>. Idempotent per
    /// (TaskId, ParticipantId): a re-run proposes nothing already assigned or already
    /// drafted, so no duplicates. SILENT — sends NO email (the queue is mail-free;
    /// only the organizer commit notifies).
    /// </summary>
    /// <remarks>
    /// <para>§1297 — the run is now <b>interval-aware</b>: each candidate's stored answer is
    /// turned into clock hours via <see cref="VolunteerDayOptions.WindowFor"/>, each task into
    /// its window via <see cref="VolunteerTaskWindow.WindowOf"/>, and nobody is proposed onto
    /// two overlapping tasks — including against assignments an organizer made by hand, which
    /// occupy their window like any other.</para>
    ///
    /// <para>🔑 <b>The result carries the SHORTFALL, not just the count.</b> Read
    /// <see cref="PlannerResult.Shortfalls"/>: on the real numbers a quarter of the slots sit
    /// outside the hours volunteers were ever asked about, and a run that reported only what it
    /// filled would read as "done".</para>
    /// </remarks>
    /// <param name="scope">
    /// Which pool to draw from. Defaults to the curated worker roster.
    /// ⚠️ <b>An EMPTY roster falls back to everyone</b> and says so in
    /// <see cref="PlannerResult.ScopeFellBackToEveryone"/> — nobody has curated it on the day
    /// this ships, and silently proposing nothing would look like a broken planner rather than
    /// an empty list.
    /// </param>
    public async Task<PlannerResult> SeedProposalsAsync(
        VolunteerStructureService.ActorContext actor,
        ParticipantRole targetRole,
        int ownerParticipantId,
        PlannerScope scope = PlannerScope.WorkersOnly,
        CancellationToken ct = default)
    {
        RequireOrganizer(actor);

        var empty = new PlannerResult(
            Array.Empty<AllocationProposal>(), Array.Empty<PlannerShortfall>(), 0, scope, false);
        if (VolunteerPlanner.QueueKindFor(targetRole) is null) return empty;

        var eventId = actor.EventId;

        var taskRows = await _db.VolunteerTasks
            .Where(t => t.EventId == eventId)
            .ToListAsync(ct);
        if (taskRows.Count == 0) return empty;

        // §1299 — the task carries its CategoryId directly (the subcategory is optional), so no
        // join or lookup is needed; 0 only ever means "no bucket" and costs just the preference.
        var tasks = taskRows
            .Select(t => new
            {
                Task = t,
                CategoryId = t.CategoryId != 0 ? t.CategoryId : (int?)null,
            })
            .ToList();

        // Candidate pool: active participants OF THE TARGET ROLE in the edition.
        var poolIds = await _db.Participants
            .Where(p => p.EventId == eventId && p.Role == targetRole && p.IsActive)
            .Select(p => p.Id)
            .ToListAsync(ct);

        // ── Scope: the worker roster ────────────────────────────────────────────────────
        var roster = await _db.VolunteerPlanningRoster
            .Where(r => r.EventId == eventId)
            .Select(r => r.ParticipantId)
            .ToListAsync(ct);

        var fellBack = false;
        if (scope == PlannerScope.WorkersOnly)
        {
            if (roster.Count == 0) fellBack = true;      // nothing curated yet — plan for everyone.
            else poolIds = poolIds.Where(roster.Contains).ToList();
        }

        // ── Availability, as WINDOWS ────────────────────────────────────────────────────
        // 🔒 Note is selected now; the pre-§1297 engine never read it, which is why the day and
        // the slot were thrown away and a setup-morning volunteer was a valid candidate for a
        // main-day afternoon task.
        var availabilityRows = await _db.VolunteerDayAvailabilities
            .Where(v => v.EventId == eventId)
            .Select(v => new { v.ParticipantId, v.Day, v.Level, v.Note })
            .ToListAsync(ct);

        var windowsByPid = availabilityRows
            .GroupBy(r => r.ParticipantId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<DateOnly, DayWindow>)g
                    .GroupBy(r => r.Day)
                    .ToDictionary(
                        d => d.Key,
                        // Several rows for one day should not happen; if they ever do, the most
                        // generous answer wins rather than an arbitrary one.
                        d => d.Select(r => VolunteerDayOptions.WindowFor(r.Day, r.Level, r.Note))
                              .OrderByDescending(w => w.Present)
                              .ThenByDescending(w => w.Window is null)
                              .First()));

        var capacityByPid = availabilityRows
            .GroupBy(r => r.ParticipantId)
            .ToDictionary(g => g.Key, g => g.Max(x => Math.Max(0, (int)x.Level)));

        // Real assignments (any participant) and THIS owner's existing drafts both
        // count toward the budget and block re-proposing the same person on a task.
        // Only EFFECTIVE assignments (§253 G7) count toward the budget — a ghost
        // (deactivated) or declined holder used to shrink the gap so the engine
        // under-proposed replacements for exactly the shifts a drop-out vacated.
        // Ineffective rows still OCCUPY the (task, person) pair for dedup safety.
        var assignments = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == eventId)
            .Select(a => new
            {
                a.TaskId,
                a.ParticipantId,
                Effective = a.Participant.IsActive
                            && a.DecisionStatus != ShiftDecisionStatus.Declined,
            })
            .ToListAsync(ct);

        var drafts = await _db.TaskAllocationDrafts
            // §1299 — the volunteer queue is SHARED per edition, so every volunteer draft counts,
            // whoever queued it; organizer-queue drafts stay per-owner as before.
            .Where(d => d.EventId == eventId
                        && (d.TargetRole == ParticipantRole.Volunteer
                            || d.OwnerParticipantId == ownerParticipantId))
            .Select(d => new { d.TaskId, d.ParticipantId })
            .ToListAsync(ct);

        var existingCountByTask = new Dictionary<int, int>();
        var occupied = new HashSet<(int TaskId, int ParticipantId)>();
        foreach (var a in assignments)
        {
            if (a.Effective)
                existingCountByTask[a.TaskId] = existingCountByTask.GetValueOrDefault(a.TaskId) + 1;
            occupied.Add((a.TaskId, a.ParticipantId));
        }
        foreach (var d in drafts)
        {
            existingCountByTask[d.TaskId] = existingCountByTask.GetValueOrDefault(d.TaskId) + 1;
            occupied.Add((d.TaskId, d.ParticipantId));
        }

        // ── What each candidate ALREADY occupies ────────────────────────────────────────
        // 🔑 This is Leth's "manual assignment consumes that timeslot", and it needs no new
        // mechanism: a hand-made assignment is a row like any other, so once the matcher is
        // interval-aware it occupies the window automatically. Drafts count too — a queued
        // proposal is a claim on that person's time until it is committed or discarded.
        var windowByTaskId = tasks.ToDictionary(
            x => x.Task.Id,
            x => (Day: x.Task.DueDate, Window: VolunteerTaskWindow.WindowOf(x.Task)));

        var heldByPid = new Dictionary<int, List<(DateOnly Day, TimeWindow Window)>>();
        var hoursByPid = new Dictionary<int, double>();
        foreach (var (taskId, pid) in assignments.Where(a => a.Effective).Select(a => (a.TaskId, a.ParticipantId))
                     .Concat(drafts.Select(d => (d.TaskId, d.ParticipantId))))
        {
            if (!windowByTaskId.TryGetValue(taskId, out var tw)) continue;
            if (tw.Day is not { } day || tw.Window is not { } w)
            {
                hoursByPid[pid] = hoursByPid.GetValueOrDefault(pid) + 1d;
                continue;
            }
            if (!heldByPid.TryGetValue(pid, out var list)) heldByPid[pid] = list = new();
            list.Add((day, w));
            hoursByPid[pid] = hoursByPid.GetValueOrDefault(pid) + w.Hours;
        }

        var candidates = poolIds
            .Select(id => new PlannerCandidate(
                id,
                capacityByPid.TryGetValue(id, out var cap) ? cap : 0,
                windowsByPid.TryGetValue(id, out var wins) ? wins : new Dictionary<DateOnly, DayWindow>(),
                heldByPid.TryGetValue(id, out var held) ? held : Array.Empty<(DateOnly, TimeWindow)>(),
                hoursByPid.GetValueOrDefault(id)))
            .ToList();

        // ── Preferred volunteers per bucket ─────────────────────────────────────────────
        var preferred = (await _db.VolunteerCategoryPreferences
                .Where(p => p.EventId == eventId)
                .Select(p => new { p.CategoryId, p.ParticipantId, p.Rank })
                .ToListAsync(ct))
            .GroupBy(p => p.CategoryId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<int, int>)g
                    .GroupBy(x => x.ParticipantId)
                    .ToDictionary(x => x.Key, x => x.Min(v => v.Rank)));

        var result = VolunteerPlanner.Plan(
            tasks.Select(x => PlannerTask.From(x.Task, x.CategoryId)),
            candidates, existingCountByTask, occupied, targetRole,
            scope, fellBack, preferred);

        if (result.Proposals.Count == 0) return result;

        var now = _clock.GetUtcNow();
        foreach (var p in result.Proposals)
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
        return result;
    }
}
