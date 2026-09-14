using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1077 stage 5 — the planner: fitting companies into the slots an organizer defined.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"i will give you specific timeslot to choose from. 25 timeslots"</i>
/// · <i>"they go into the planner"</i> · and the original requirement, <i>"photo slots spread over
/// pre-day and main day, decided by which ticket types the company bought"</i>.</para>
///
/// <para>🔴 <b>Two rules carry the feature, and both are asserted as OUTCOMES rather than as calls:</b>
/// a company whose people all hold 1-day tickets can never be given a pre-day slot (they are not
/// there), and a published time is never moved (their colleagues already have it).</para>
/// </remarks>
public sealed class GroupPhotoPlannerTests
{
    private static readonly DateTimeOffset PreDay = new(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MainDay = new(2027, 2, 10, 10, 0, 0, TimeSpan.Zero);

    private static List<GroupPhotoSlotOption> Slots(int preDay, int mainDay)
    {
        var slots = new List<GroupPhotoSlotOption>();
        var id = 1;
        for (var i = 0; i < preDay; i++)
            slots.Add(new GroupPhotoSlotOption(id++, PreDay.AddMinutes(15 * i), IsPreDay: true));
        for (var i = 0; i < mainDay; i++)
            slots.Add(new GroupPhotoSlotOption(id++, MainDay.AddMinutes(15 * i), IsPreDay: false));
        return slots;
    }

    private static GroupPhotoCandidate Company(
        int id, string name, bool canDoPreDay = true, int? pinned = null) =>
        new(id, name, canDoPreDay, pinned);

    /// <summary>
    /// 🔴 The constraint. Their people are not at the venue on the pre-day, so a pre-day slot would
    /// photograph an empty space — this is not a preference the planner may trade away.
    /// </summary>
    [Fact]
    public void A_one_day_only_company_is_never_given_a_pre_day_slot()
    {
        var plan = GroupPhotoPlanner.Plan(
            new[] { Company(1, "OneDay Ltd", canDoPreDay: false) },
            Slots(preDay: 5, mainDay: 5));

        var a = Assert.Single(plan.Assignments);
        Assert.Equal(MainDay.Date, a.StartUtc.UtcDateTime.Date);
    }

    /// <summary>
    /// 🔑 Rule 2: the flexible companies go to the PRE-DAY, keeping the scarce main day for those
    /// with nowhere else to go. Asserted with exactly enough slots that the naive order would fail.
    /// </summary>
    [Fact]
    public void The_flexible_companies_take_the_pre_day_so_the_main_day_is_left_for_those_who_need_it()
    {
        var plan = GroupPhotoPlanner.Plan(
            new[]
            {
                Company(1, "Flexible A"),
                Company(2, "Flexible B"),
                Company(3, "MainDayOnly X", canDoPreDay: false),
                Company(4, "MainDayOnly Y", canDoPreDay: false),
            },
            Slots(preDay: 2, mainDay: 2));

        Assert.Empty(plan.Unplaced);
        var byName = plan.Assignments.ToDictionary(a => a.CompanyName, a => a.StartUtc.UtcDateTime.Date);
        Assert.Equal(PreDay.Date, byName["Flexible A"]);
        Assert.Equal(PreDay.Date, byName["Flexible B"]);
        Assert.Equal(MainDay.Date, byName["MainDayOnly X"]);
        Assert.Equal(MainDay.Date, byName["MainDayOnly Y"]);
    }

    /// <summary>
    /// 🔒 A published time is PINNED. Adding a company in January must not move a slot eleven people
    /// already have in their calendars.
    /// </summary>
    [Fact]
    public void A_published_company_keeps_its_exact_slot_when_the_plan_is_re_run()
    {
        var slots = Slots(preDay: 3, mainDay: 3);
        var pinnedSlot = slots.First(s => !s.IsPreDay);

        var plan = GroupPhotoPlanner.Plan(
            new[]
            {
                Company(1, "Already Told", pinned: pinnedSlot.SlotId),
                Company(2, "New Arrival"),
                Company(3, "Another New"),
            },
            slots);

        var told = plan.Assignments.Single(a => a.CompanyName == "Already Told");
        Assert.Equal(pinnedSlot.StartUtc, told.StartUtc);
        Assert.True(told.WasAlreadyPublished);
        // …and nobody else was put in that slot.
        Assert.Single(plan.Assignments, a => a.StartUtc == pinnedSlot.StartUtc);
    }

    /// <summary>
    /// 🔴 Nobody is silently dropped. A planner that quietly places 23 of 25 is worse than one that
    /// refuses: two coordinators would never hear from us, and it would surface on the day.
    /// </summary>
    [Fact]
    public void Companies_that_do_not_fit_are_reported_with_a_reason()
    {
        var plan = GroupPhotoPlanner.Plan(
            new[] { Company(1, "A"), Company(2, "B"), Company(3, "C") },
            Slots(preDay: 1, mainDay: 1));

        Assert.Equal(2, plan.Assignments.Count);
        var (name, reason) = Assert.Single(plan.Unplaced);
        Assert.Equal("C", name);
        Assert.Contains("No slot left", reason);
    }

    /// <summary>
    /// ⚠️ A 1-day-only company with no main-day slot gets a reason that says WHY it cannot simply
    /// use the pre-day slot sitting empty next to it.
    /// </summary>
    [Fact]
    public void A_one_day_company_with_no_main_day_slot_is_told_why_the_free_pre_day_slot_will_not_do()
    {
        var plan = GroupPhotoPlanner.Plan(
            new[] { Company(1, "OneDay Ltd", canDoPreDay: false) },
            Slots(preDay: 5, mainDay: 0));

        Assert.Empty(plan.Assignments);
        var (_, reason) = Assert.Single(plan.Unplaced);
        Assert.Contains("1-day tickets", reason);
        Assert.Equal(5, plan.UnusedSlots.Count);
    }

    /// <summary>
    /// ⚠️ A company published against a slot that has since been DELETED is surfaced, not silently
    /// re-planned: they are holding a calendar entry for a time that no longer exists.
    /// </summary>
    [Fact]
    public void A_company_pinned_to_a_deleted_slot_is_reported()
    {
        var plan = GroupPhotoPlanner.Plan(
            new[] { Company(1, "Orphaned", pinned: 9999) },
            Slots(preDay: 2, mainDay: 2));

        Assert.Empty(plan.Assignments);
        var (name, reason) = Assert.Single(plan.Unplaced);
        Assert.Equal("Orphaned", name);
        Assert.Contains("has since been removed", reason);
    }

    /// <summary>
    /// 🔑 Deterministic: the same input gives the same plan, every time. That is what makes it safe
    /// to press "propose" repeatedly while arguing about the result.
    /// </summary>
    [Fact]
    public void The_same_input_always_produces_the_same_plan()
    {
        var candidates = new[]
        {
            Company(1, "Zulu"), Company(2, "Alpha"),
            Company(3, "Mike", canDoPreDay: false), Company(4, "Bravo"),
        };

        var first = GroupPhotoPlanner.Plan(candidates, Slots(2, 2));
        var second = GroupPhotoPlanner.Plan(candidates, Slots(2, 2));

        Assert.Equal(
            first.Assignments.Select(a => (a.CompanyName, a.StartUtc)),
            second.Assignments.Select(a => (a.CompanyName, a.StartUtc)));
    }

    /// <summary>Two companies are never put in front of the camera at the same moment.</summary>
    [Fact]
    public void No_two_companies_share_a_slot()
    {
        var plan = GroupPhotoPlanner.Plan(
            Enumerable.Range(1, 10).Select(i => Company(i, $"Company {i:00}")).ToList(),
            Slots(preDay: 5, mainDay: 5));

        Assert.Equal(10, plan.Assignments.Count);
        Assert.Equal(10, plan.Assignments.Select(a => a.StartUtc).Distinct().Count());
        Assert.Empty(plan.UnusedSlots);
    }

    /// <summary>An edition with no pre-day still plans — everything lands on the main day.</summary>
    [Fact]
    public void With_no_pre_day_slots_everything_goes_to_the_main_day()
    {
        var plan = GroupPhotoPlanner.Plan(
            new[] { Company(1, "A"), Company(2, "B", canDoPreDay: false) },
            Slots(preDay: 0, mainDay: 4));

        Assert.Equal(2, plan.Assignments.Count);
        Assert.All(plan.Assignments, a => Assert.Equal(MainDay.Date, a.StartUtc.UtcDateTime.Date));
    }
}
