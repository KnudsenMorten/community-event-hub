using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Volunteers;

/// <summary>Which pool a planner run draws from.</summary>
public enum PlannerScope
{
    /// <summary>The curated worker roster only (<see cref="VolunteerPlanningRosterEntry"/>) — the default.</summary>
    WorkersOnly = 0,
    /// <summary>Every eligible volunteer, roster or not.</summary>
    Everyone = 1,
}

/// <summary>Why a task could not be filled — the distinction that makes a shortfall actionable.</summary>
public enum ShortfallReason
{
    /// <summary>Not short.</summary>
    None = 0,
    /// <summary>Nobody in the pool stated availability covering this task's hours. More people would not help; a different WINDOW would.</summary>
    OutsideStatedHours = 1,
    /// <summary>People are available at that hour, but every one of them is already holding an overlapping task. More people WOULD help.</summary>
    EveryoneBusy = 2,
    /// <summary>The pool is empty or nobody is present on that day at all.</summary>
    NobodyAvailable = 3,
    /// <summary>People were eligible and free — there were simply fewer of them than the task needs.</summary>
    NotEnoughPeople = 4,
}

/// <summary>One task the run could not fully staff, with the reason and the numbers behind it.</summary>
public sealed record PlannerShortfall(
    int TaskId,
    string TaskTitle,
    DateOnly? Day,
    TimeWindow? Window,
    int Needed,
    int Covered,
    ShortfallReason Reason)
{
    public int Short => Math.Max(0, Needed - Covered);

    /// <summary>One sentence an organizer can act on.</summary>
    public string Explain() => Reason switch
    {
        ShortfallReason.OutsideStatedHours =>
            $"No one in scope said they are available {Window?.ToString() ?? "at that time"}"
            + (Day is { } d ? $" on {d:ddd dd MMM}" : "")
            + " — adding people will not fix this; the day's availability window would have to change.",
        ShortfallReason.EveryoneBusy =>
            "Everyone available at that hour is already on an overlapping task — this needs more people, not a different window.",
        ShortfallReason.NotEnoughPeople =>
            "Everyone eligible and free was used — the pool is smaller than the task needs.",
        ShortfallReason.NobodyAvailable =>
            "Nobody in scope is available on that day at all.",
        _ => "Covered.",
    };
}

/// <summary>What one planner run produced.</summary>
/// <remarks>
/// 🔑 <b>The shortfalls are part of the RESULT, not a log line.</b> Against the real ELDK27
/// numbers the setup day runs at ~85% utilisation and the main day at ~81% before any break or
/// travel, and ~25% of the slots sit outside the hours volunteers were ever asked about. A
/// planner that returned only its proposals on data like that would read as "done" while a
/// quarter of the plan was silently unstaffed.
/// </remarks>
public sealed record PlannerResult(
    IReadOnlyList<AllocationProposal> Proposals,
    IReadOnlyList<PlannerShortfall> Shortfalls,
    int CandidatesConsidered,
    PlannerScope Scope,
    bool ScopeFellBackToEveryone)
{
    public int SlotsShort => Shortfalls.Sum(s => s.Short);

    /// <summary>
    /// The one-paragraph run report an organizer reads after pressing Auto-assign.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It always names the shortfall</b>, and it names it by REASON. "Proposed 140
    /// assignments" on a plan that is 57 slots short is a true sentence that leaves the reader
    /// believing the opposite of the truth — and the two reasons need opposite responses
    /// (recruit more people vs. change the hours people were asked about).
    /// </remarks>
    public string Summarize()
    {
        var parts = new List<string>();

        parts.Add(Proposals.Count == 0
            ? "Auto-assign proposed nothing new (tasks already covered, or nobody available in the remaining windows)."
            : $"Auto-assign proposed {Proposals.Count} assignment(s) — review the queue below, then Commit.");

        if (SlotsShort > 0)
        {
            var byReason = Shortfalls
                .GroupBy(s => s.Reason)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.Short));

            var reasons = new List<string>();
            if (byReason.TryGetValue(ShortfallReason.OutsideStatedHours, out var outside))
                reasons.Add($"{outside} outside the hours volunteers were asked about");
            if (byReason.TryGetValue(ShortfallReason.EveryoneBusy, out var busy))
                reasons.Add($"{busy} where everyone free is already on an overlapping task");
            if (byReason.TryGetValue(ShortfallReason.NotEnoughPeople, out var few))
                reasons.Add($"{few} where the pool ran out");
            if (byReason.TryGetValue(ShortfallReason.NobodyAvailable, out var none))
                reasons.Add($"{none} on days nobody in scope is present");

            parts.Add($"COULD NOT FILL {SlotsShort} slot(s) across {Shortfalls.Count} task(s): "
                      + string.Join("; ", reasons) + ".");
        }

        if (ScopeFellBackToEveryone)
            parts.Add("The worker roster is empty, so this run planned for EVERY active volunteer. "
                      + "Curate it under Volunteers → Worker roster to scope future runs.");
        else if (Scope == PlannerScope.WorkersOnly)
            parts.Add($"Planned from the worker roster ({CandidatesConsidered} in scope); everyone else is hand-placed.");

        return string.Join(" ", parts);
    }
}

/// <summary>A task as the pure matcher sees it: the routing band plus its resolved window.</summary>
public sealed record PlannerTask(
    int Id,
    string Title,
    int ResourcesNeeded,
    string? ResponsibleTeam,
    DateOnly? Day,
    TimeWindow? Window,
    int? CategoryId)
{
    public static PlannerTask From(VolunteerTask t, int? categoryId = null) =>
        new(t.Id, t.Title, t.ResourcesNeeded, t.ResponsibleTeam, t.DueDate,
            VolunteerTaskWindow.WindowOf(t), categoryId);
}

/// <summary>
/// A candidate as the pure matcher sees it: who they are, when they said they can work, what
/// they are already holding, and how loaded they already are.
/// </summary>
/// <param name="Availability">Day → what that day's answer means in clock terms. A day absent from the map = no answer = not available.</param>
/// <param name="Held">Intervals they already occupy (existing assignments and drafts). Grows during a run as the matcher proposes.</param>
/// <param name="HoursAllocated">Hours already committed — the fairness tie-break's starting point. Also grows during the run.</param>
public sealed record PlannerCandidate(
    int ParticipantId,
    int Capacity,
    IReadOnlyDictionary<DateOnly, DayWindow> Availability,
    IReadOnlyList<(DateOnly Day, TimeWindow Window)> Held,
    double HoursAllocated);

/// <summary>
/// §1297 — the interval-aware volunteer matcher. PURE: no DB, no clock, no I/O, fully
/// deterministic. <see cref="AvailabilityAutoAssignEngine"/> loads the state and persists the
/// result; everything that decides WHO goes on WHAT lives here so it can be tested directly.
/// </summary>
/// <remarks>
/// <para>It replaces a matcher that scored each candidate with a single capacity scalar —
/// <c>Max(Level)</c> across ALL days, with the day and the slot thrown away. That had three
/// consequences worth naming, because each one produced plausible-looking nonsense:</para>
/// <list type="number">
/// <item>A volunteer available only on the setup morning scored 100 and was a valid candidate
/// for a main-day afternoon task.</item>
/// <item>There was no per-run memory, so the top-ranked volunteer was proposed onto <i>every</i>
/// short task — including tasks running at the same hour.</item>
/// <item>The only overlap notion anywhere flagged two tasks on the same DATE as a "possible
/// double-booking", in simulation only, as a warning.</item>
/// </list>
///
/// <para>Leth's requirements map onto it directly: honour stated availability
/// (<see cref="DayWindow.CanCover"/>), a duration per task (<see cref="TimeWindow.Hours"/>),
/// preferred volunteers per category ranked higher (<c>preferredByCategory</c>), manual
/// assignment consumes that timeslot (manual rows arrive in <see cref="PlannerCandidate.Held"/>
/// like any other), and no overlapping timeslots (<see cref="TimeWindow.Overlaps"/>).</para>
/// </remarks>
public static class VolunteerPlanner
{
    /// <summary>
    /// Compute a plan. Deterministic for a given input — the same state always produces the
    /// same proposals, in the same order.
    /// </summary>
    /// <param name="preferredByCategory">CategoryId → (ParticipantId → rank). Lowest rank first.</param>
    public static PlannerResult Plan(
        IEnumerable<PlannerTask> tasks,
        IReadOnlyCollection<PlannerCandidate> candidates,
        IReadOnlyDictionary<int, int> existingCountByTask,
        ISet<(int TaskId, int ParticipantId)> occupied,
        ParticipantRole targetRole,
        PlannerScope scope = PlannerScope.WorkersOnly,
        bool scopeFellBackToEveryone = false,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>>? preferredByCategory = null)
    {
        var wanted = QueueKindFor(targetRole);
        if (wanted is null)
            return new PlannerResult(Array.Empty<AllocationProposal>(), Array.Empty<PlannerShortfall>(), 0, scope, scopeFellBackToEveryone);

        // Mutable per-run state, seeded from what each candidate already holds. A proposal made
        // in THIS run occupies its window immediately, which is the fix for "the same person
        // proposed onto every short task".
        var state = candidates
            .Where(c => c.Capacity > 0)
            .ToDictionary(
                c => c.ParticipantId,
                c => new RunState(c, c.Held.ToList(), c.HoursAllocated));

        var routed = tasks
            .Where(t => ResponsibleTeamRouter.Route(t.ResponsibleTeam) == wanted.Value)
            .ToList();

        // ── Ordering: HARDEST FIRST ──────────────────────────────────────────────────────
        // 🔴 Not a strategy preference. Walking tasks in creation order lets a "fair" pick
        // spend the only eligible person on a task anyone could have done, leaving the
        // constrained task unfillable — a failure the planner then reports as a shortfall that
        // its own ordering caused. Fewest eligible people first; then longest (the big blocks
        // are the hard ones to place); then id, so the run is reproducible.
        var eligibleCount = routed.ToDictionary(
            t => t.Id,
            t => state.Values.Count(s => IsAvailableFor(s.Candidate, t)));

        var ordered = routed
            .OrderBy(t => eligibleCount[t.Id])
            .ThenByDescending(t => t.Window?.Hours ?? 0d)
            .ThenBy(t => t.Id)
            .ToList();

        var proposals = new List<AllocationProposal>();
        var shortfalls = new List<PlannerShortfall>();

        foreach (var task in ordered)
        {
            var have = existingCountByTask.TryGetValue(task.Id, out var n) ? n : 0;
            var gap = task.ResourcesNeeded - have;
            if (gap <= 0) continue;

            var preferred = task.CategoryId is { } cid && preferredByCategory is not null
                                                       && preferredByCategory.TryGetValue(cid, out var p)
                ? p
                : null;

            // ── Ranking ──────────────────────────────────────────────────────────────────
            // preferred-for-this-bucket (by rank) → fewest hours already allocated (fairness)
            // → capacity → id. The fairness term is the tie-break, not the driver: the pool is
            // the worker roster, who opted into being worked, so coverage comes first.
            var ranked = state.Values
                .Where(s => IsAvailableFor(s.Candidate, task))
                .OrderBy(s => preferred is not null && preferred.TryGetValue(s.Candidate.ParticipantId, out var r) ? r : int.MaxValue)
                .ThenBy(s => s.Hours)
                .ThenByDescending(s => s.Candidate.Capacity)
                .ThenBy(s => s.Candidate.ParticipantId)
                .ToList();

            var filledHere = 0;
            var blockedByOverlap = false;

            foreach (var s in ranked)
            {
                if (gap == 0) break;
                if (occupied.Contains((task.Id, s.Candidate.ParticipantId))) continue;

                if (Collides(s, task)) { blockedByOverlap = true; continue; }

                proposals.Add(new AllocationProposal(task.Id, s.Candidate.ParticipantId, targetRole));
                occupied.Add((task.Id, s.Candidate.ParticipantId));
                s.Take(task);
                gap--;
                filledHere++;
            }

            if (gap > 0)
            {
                // 🔑 The reason matters more than the number: "nobody said they work at that
                // hour" and "everybody who does is already busy" call for OPPOSITE responses.
                // The first is not fixable by recruiting — the day's availability window would
                // have to change, or the task would. The second is exactly what more people fix.
                var reason =
                    ranked.Count == 0
                        ? (PresentOnDay(state.Values, task)
                            ? ShortfallReason.OutsideStatedHours
                            : ShortfallReason.NobodyAvailable)
                    : blockedByOverlap ? ShortfallReason.EveryoneBusy
                    : ShortfallReason.NotEnoughPeople;

                shortfalls.Add(new PlannerShortfall(
                    task.Id, task.Title, task.Day, task.Window,
                    task.ResourcesNeeded, have + filledHere, reason));
            }
        }

        // Report in the order an organizer wants to read it: biggest hole first, then by day.
        var report = shortfalls
            .OrderByDescending(s => s.Short)
            .ThenBy(s => s.Day ?? DateOnly.MaxValue)
            .ThenBy(s => s.TaskId)
            .ToList();

        return new PlannerResult(proposals, report, state.Count, scope, scopeFellBackToEveryone);
    }

    /// <summary>The queue kind a target role's proposals belong to, or null when it has none.</summary>
    internal static AllocationQueueKind? QueueKindFor(ParticipantRole targetRole) => targetRole switch
    {
        ParticipantRole.Volunteer => AllocationQueueKind.Volunteer,
        ParticipantRole.Organizer => AllocationQueueKind.Organizer,
        _ => null,
    };

    /// <summary>
    /// Could this candidate do this task at all, ignoring what they are already holding?
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A task with no day is UNSCHEDULED and everybody with capacity can do it.</b> That is
    /// the pre-§1297 behaviour and it is kept deliberately: plenty of tasks carry no DueDate, and
    /// treating "no date recorded" as "nobody can do it" would empty the plan rather than improve it.
    /// </remarks>
    private static bool IsAvailableFor(PlannerCandidate c, PlannerTask task)
    {
        if (task.Day is not { } day) return c.Capacity > 0;
        return c.Availability.TryGetValue(day, out var dw) && dw.CanCover(task.Window);
    }

    /// <summary>
    /// Is ANYBODY in the pool present on this task's day, whatever hours they said? This is what
    /// separates "they are here, just not at that hour" (fix the window) from "nobody is here at
    /// all" (fix the day) — the two shortfalls an organizer answers differently.
    /// </summary>
    private static bool PresentOnDay(IEnumerable<RunState> pool, PlannerTask task)
    {
        if (task.Day is not { } day) return true; // unscheduled: the day is not the obstacle.
        return pool.Any(s => s.Candidate.Availability.TryGetValue(day, out var dw) && dw.Present);
    }

    /// <summary>Does taking this task double-book the candidate?</summary>
    private static bool Collides(RunState s, PlannerTask task)
    {
        if (task.Day is not { } day || task.Window is not { } w) return false;
        return s.Held.Any(h => h.Day == day && h.Window.Overlaps(w));
    }

    /// <summary>Per-run mutable view of a candidate: what they hold and how loaded they are.</summary>
    private sealed class RunState(PlannerCandidate candidate, List<(DateOnly Day, TimeWindow Window)> held, double hours)
    {
        public PlannerCandidate Candidate { get; } = candidate;
        public List<(DateOnly Day, TimeWindow Window)> Held { get; } = held;
        public double Hours { get; private set; } = hours;

        public void Take(PlannerTask task)
        {
            if (task.Day is { } day && task.Window is { } w)
            {
                Held.Add((day, w));
                Hours += w.Hours;
                return;
            }

            // An unscheduled task still costs the person something, so it must count toward
            // fairness — otherwise the matcher happily stacks every undated task on one person
            // because their hours never move. One hour is a placeholder, and an honest one:
            // the task has no recorded length to use instead.
            Hours += 1d;
        }
    }
}
