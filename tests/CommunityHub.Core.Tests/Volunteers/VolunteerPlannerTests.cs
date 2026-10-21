using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §1297 — the interval-aware matcher. PURE, so every rule Leth asked for can be asserted
/// directly rather than inferred from what ended up in the draft queue.
///
/// <para>Leth's five requirements, and the test that pins each:</para>
/// <list type="bullet">
/// <item>honour stated availability → <see cref="Only_proposes_people_whose_window_CONTAINS_the_task"/></item>
/// <item>a duration per task → the windows themselves, plus <see cref="Fairness_spreads_HOURS_not_task_counts"/></item>
/// <item>preferred volunteers per category rank higher → <see cref="A_preferred_volunteer_is_ranked_first_for_that_bucket"/></item>
/// <item>manual assignment consumes that timeslot → <see cref="An_existing_assignment_blocks_an_overlapping_proposal"/></item>
/// <item>no overlapping timeslots → <see cref="Nobody_is_proposed_onto_two_tasks_that_overlap_IN_THE_SAME_RUN"/></item>
/// </list>
/// </summary>
public sealed class VolunteerPlannerTests
{
    private static readonly DateOnly Day = new(2027, 2, 10);
    private static readonly DateOnly OtherDay = new(2027, 2, 9);

    private static TimeWindow W(int h1, int m1, int h2, int m2) =>
        new(new TimeOnly(h1, m1), new TimeOnly(h2, m2));

    private static PlannerTask Task(int id, int needed, TimeWindow? window, DateOnly? day = null, int? category = null) =>
        new(id, $"Task {id}", needed, ResponsibleTeamRouter.VolunteerTeam, day ?? Day, window, category);

    /// <summary>A candidate available for one window on <see cref="Day"/>, holding nothing.</summary>
    private static PlannerCandidate Person(int id, TimeWindow? window, DateOnly? day = null) =>
        new(id, 100,
            new Dictionary<DateOnly, DayWindow> { [day ?? Day] = new(true, window) },
            Array.Empty<(DateOnly, TimeWindow)>(), 0d);

    private static PlannerResult Run(
        IEnumerable<PlannerTask> tasks,
        IEnumerable<PlannerCandidate> people,
        IReadOnlyDictionary<int, int>? existing = null,
        ISet<(int, int)>? occupied = null,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>>? preferred = null) =>
        VolunteerPlanner.Plan(
            tasks, people.ToList(),
            existing ?? new Dictionary<int, int>(),
            occupied ?? new HashSet<(int, int)>(),
            ParticipantRole.Volunteer,
            PlannerScope.Everyone, false, preferred);

    // ── Availability ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_proposes_people_whose_window_CONTAINS_the_task()
    {
        // 🔴 The headline bug this replaces: the old engine scored capacity as Max(Level) across
        // ALL days with the day and slot thrown away, so a morning-only volunteer was a valid
        // candidate for an afternoon task.
        var afternoonTask = Task(1, needed: 2, W(13, 0, 16, 0));

        var morningOnly = Person(10, W(6, 40, 12, 0));
        var afternoon = Person(11, W(12, 0, 17, 0));
        var fullDay = Person(12, W(6, 40, 17, 0));

        var r = Run(new[] { afternoonTask }, new[] { morningOnly, afternoon, fullDay });

        Assert.Equal(2, r.Proposals.Count);
        Assert.DoesNotContain(r.Proposals, p => p.ParticipantId == 10);
    }

    [Fact]
    public void Somebody_who_leaves_mid_task_does_not_count_as_covering_it()
    {
        // Eligibility is CONTAINMENT, not overlap. Being there for part of a task is not staffing it.
        var task = Task(1, needed: 1, W(10, 0, 14, 0));
        var partial = Person(10, W(6, 40, 12, 0));

        var r = Run(new[] { task }, new[] { partial });

        Assert.Empty(r.Proposals);
        Assert.Equal(ShortfallReason.OutsideStatedHours, Assert.Single(r.Shortfalls).Reason);
    }

    [Fact]
    public void A_person_who_is_not_there_that_DAY_is_never_proposed()
    {
        var task = Task(1, needed: 1, W(9, 0, 12, 0));
        var wrongDay = Person(10, W(6, 40, 17, 0), OtherDay);

        var r = Run(new[] { task }, new[] { wrongDay });

        Assert.Empty(r.Proposals);
        Assert.Equal(ShortfallReason.NobodyAvailable, Assert.Single(r.Shortfalls).Reason);
    }

    // ── No double-booking ────────────────────────────────────────────────────────────────

    [Fact]
    public void Nobody_is_proposed_onto_two_tasks_that_overlap_IN_THE_SAME_RUN()
    {
        // 🔴 The second failure of the old engine: the candidate list was re-walked per task with
        // NO memory, so the top-ranked volunteer was proposed onto every short task — including
        // ones running at the same hour.
        var a = Task(1, needed: 1, W(9, 0, 12, 0));
        var b = Task(2, needed: 1, W(10, 0, 11, 0));   // overlaps a
        var only = Person(10, W(6, 40, 17, 0));

        var r = Run(new[] { a, b }, new[] { only });

        Assert.Single(r.Proposals);
        var unfilled = Assert.Single(r.Shortfalls);
        Assert.Equal(ShortfallReason.EveryoneBusy, unfilled.Reason);
    }

    [Fact]
    public void Back_to_back_tasks_CAN_go_to_the_same_person()
    {
        // The other side of the half-open interval: consecutive shifts are not a double-booking,
        // and refusing them would break most of the real plan.
        var a = Task(1, needed: 1, W(9, 0, 12, 0));
        var b = Task(2, needed: 1, W(12, 0, 15, 0));
        var only = Person(10, W(6, 40, 17, 0));

        var r = Run(new[] { a, b }, new[] { only });

        Assert.Equal(2, r.Proposals.Count);
        Assert.All(r.Proposals, p => Assert.Equal(10, p.ParticipantId));
        Assert.Empty(r.Shortfalls);
    }

    [Fact]
    public void An_existing_assignment_blocks_an_overlapping_proposal()
    {
        // 🔑 Leth's "manual assignment consumes that timeslot" needs NO new mechanism: a hand-made
        // assignment arrives as a held interval, and the interval model does the rest.
        var task = Task(1, needed: 1, W(10, 0, 11, 0));

        var busy = new PlannerCandidate(
            10, 100,
            new Dictionary<DateOnly, DayWindow> { [Day] = new(true, W(6, 40, 17, 0)) },
            new[] { (Day, W(9, 30, 10, 30)) },   // hand-placed earlier, overlapping
            1d);

        var r = Run(new[] { task }, new[] { busy });

        Assert.Empty(r.Proposals);
        Assert.Equal(ShortfallReason.EveryoneBusy, Assert.Single(r.Shortfalls).Reason);
    }

    // ── Ranking ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_preferred_volunteer_is_ranked_first_for_that_bucket()
    {
        var task = Task(1, needed: 1, W(9, 0, 12, 0), category: 7);
        var ordinary = Person(10, W(6, 40, 17, 0));
        var preferredPerson = Person(99, W(6, 40, 17, 0));   // higher id — loses on every other tie-break

        var prefs = new Dictionary<int, IReadOnlyDictionary<int, int>>
        {
            [7] = new Dictionary<int, int> { [99] = 0 },
        };

        var r = Run(new[] { task }, new[] { ordinary, preferredPerson }, preferred: prefs);

        Assert.Equal(99, Assert.Single(r.Proposals).ParticipantId);
    }

    [Fact]
    public void A_preference_does_NOT_override_availability()
    {
        // ⚠️ Preferred is a preference, not a reservation. Honouring it past the window would
        // produce an assignment that reads as decided and cannot actually be worked.
        var task = Task(1, needed: 1, W(13, 0, 16, 0), category: 7);
        var preferredButMorning = Person(99, W(6, 40, 12, 0));
        var availableAfternoon = Person(10, W(12, 0, 17, 0));

        var prefs = new Dictionary<int, IReadOnlyDictionary<int, int>>
        {
            [7] = new Dictionary<int, int> { [99] = 0 },
        };

        var r = Run(new[] { task }, new[] { preferredButMorning, availableAfternoon }, preferred: prefs);

        Assert.Equal(10, Assert.Single(r.Proposals).ParticipantId);
    }

    [Fact]
    public void Fairness_spreads_HOURS_not_task_counts()
    {
        // The tie-break is hours already allocated, so the person carrying a 7-hour block is not
        // handed the next short task ahead of someone idle.
        var task = Task(1, needed: 1, W(15, 0, 16, 0));

        var loaded = new PlannerCandidate(
            10, 100,
            new Dictionary<DateOnly, DayWindow> { [Day] = new(true, W(6, 40, 17, 0)) },
            new[] { (Day, W(6, 40, 13, 40)) }, 7d);

        var idle = new PlannerCandidate(
            11, 100,
            new Dictionary<DateOnly, DayWindow> { [Day] = new(true, W(6, 40, 17, 0)) },
            Array.Empty<(DateOnly, TimeWindow)>(), 0d);

        var r = Run(new[] { task }, new[] { loaded, idle });

        Assert.Equal(11, Assert.Single(r.Proposals).ParticipantId);
    }

    [Fact]
    public void The_HARDEST_task_is_filled_first()
    {
        // 🔴 Not a strategy preference. In creation order the "fair" pick spends the only eligible
        // person on the task anyone could have done, and the constrained task is then unfillable —
        // a shortfall the planner's own ordering caused.
        var easy = Task(1, needed: 1, W(9, 0, 10, 0));       // both can do it
        var hard = Task(2, needed: 1, W(16, 0, 17, 0));      // only the full-day person can

        var morning = Person(10, W(6, 40, 12, 0));
        var fullDay = Person(11, W(6, 40, 17, 0));

        var r = Run(new[] { easy, hard }, new[] { morning, fullDay });

        Assert.Equal(2, r.Proposals.Count);
        Assert.Empty(r.Shortfalls);
        Assert.Equal(11, r.Proposals.Single(p => p.TaskId == 2).ParticipantId);
        Assert.Equal(10, r.Proposals.Single(p => p.TaskId == 1).ParticipantId);
    }

    // ── Reporting ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_shortfall_distinguishes_a_WINDOW_problem_from_a_PEOPLE_problem()
    {
        // 🔑 The two need opposite responses — recruiting does not help the first, and changing
        // the availability window does not help the second.
        var outsideHours = Task(1, needed: 3, W(17, 30, 21, 0));   // nobody said they work evenings
        var tooFewPeople = Task(2, needed: 3, W(9, 0, 10, 0));     // everybody can, there are just 2

        var a = Person(10, W(6, 40, 12, 0));
        var b = Person(11, W(6, 40, 12, 0));

        var r = Run(new[] { outsideHours, tooFewPeople }, new[] { a, b });

        var evening = r.Shortfalls.Single(s => s.TaskId == 1);
        Assert.Equal(ShortfallReason.OutsideStatedHours, evening.Reason);
        Assert.Equal(3, evening.Short);
        Assert.Contains("adding people will not fix this", evening.Explain());

        var thin = r.Shortfalls.Single(s => s.TaskId == 2);
        Assert.Equal(ShortfallReason.NotEnoughPeople, thin.Reason);
        Assert.Equal(1, thin.Short);   // 2 of 3 filled

        Assert.Equal(4, r.SlotsShort);
        Assert.Contains("COULD NOT FILL 4 slot(s)", r.Summarize());
    }

    [Fact]
    public void An_empty_roster_is_reported_rather_than_left_looking_like_a_broken_planner()
    {
        var r = VolunteerPlanner.Plan(
            new[] { Task(1, 1, W(9, 0, 10, 0)) },
            new[] { Person(10, W(6, 40, 17, 0)) },
            new Dictionary<int, int>(), new HashSet<(int, int)>(),
            ParticipantRole.Volunteer,
            PlannerScope.WorkersOnly, scopeFellBackToEveryone: true);

        Assert.True(r.ScopeFellBackToEveryone);
        Assert.Contains("worker roster is empty", r.Summarize());
    }

    [Fact]
    public void A_covered_task_produces_no_shortfall_row()
    {
        var r = Run(new[] { Task(1, needed: 1, W(9, 0, 10, 0)) }, new[] { Person(10, W(6, 40, 17, 0)) });

        Assert.Empty(r.Shortfalls);
        Assert.Equal(0, r.SlotsShort);
        Assert.DoesNotContain("COULD NOT FILL", r.Summarize());
    }

    // ── Things that must NOT have changed ────────────────────────────────────────────────

    [Fact]
    public void An_UNSCHEDULED_task_still_gets_filled_by_anyone_with_capacity()
    {
        // ⚠️ Plenty of tasks carry no DueDate. Treating "no time recorded" as "nobody can do it"
        // would empty the plan rather than improve it.
        var undated = new PlannerTask(1, "Undated", 2, ResponsibleTeamRouter.VolunteerTeam, null, null, null);
        var people = new[] { Person(10, null), Person(11, null) };

        var r = Run(new[] { undated }, people);

        Assert.Equal(2, r.Proposals.Count);
    }

    [Fact]
    public void Tracked_only_tasks_are_still_skipped_entirely()
    {
        var tracked = new PlannerTask(1, "Photo", 5, "Photo", Day, W(9, 0, 12, 0), null);

        var r = Run(new[] { tracked }, new[] { Person(10, W(6, 40, 17, 0)) });

        Assert.Empty(r.Proposals);
        Assert.Empty(r.Shortfalls);   // not ours to staff, so not our shortfall either
    }

    [Fact]
    public void Somebody_already_on_the_task_is_never_re_proposed()
    {
        var task = Task(1, needed: 2, W(9, 0, 12, 0));
        var already = Person(10, W(6, 40, 17, 0));
        var fresh = Person(11, W(6, 40, 17, 0));

        var r = Run(new[] { task }, new[] { already, fresh },
            existing: new Dictionary<int, int> { [1] = 1 },
            occupied: new HashSet<(int, int)> { (1, 10) });

        Assert.Equal(11, Assert.Single(r.Proposals).ParticipantId);
    }

    [Fact]
    public void The_run_is_deterministic()
    {
        var tasks = new[] { Task(1, 1, W(9, 0, 12, 0)), Task(2, 1, W(13, 0, 16, 0)) };
        var people = new[] { Person(10, W(6, 40, 17, 0)), Person(11, W(6, 40, 17, 0)) };

        var first = Run(tasks, people).Proposals.Select(p => (p.TaskId, p.ParticipantId)).ToArray();
        var second = Run(tasks, people).Proposals.Select(p => (p.TaskId, p.ParticipantId)).ToArray();

        Assert.Equal(first, second);
    }
}
