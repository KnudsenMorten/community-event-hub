using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1181 — THE TIER ROUNDS ARE NAMED DATES, SO THEY ARE WINDOWS AND NOT FLOORS.
///
/// <para>Operator 2026-09-12: <i>"i would like to run sponsor category some posts, so round 1 runs
/// from dec 15 and round 2 runs from jan 15"</i>.</para>
///
/// <para>🔑 <b>The distinction §1179 turned on, applied the other way.</b> A sponsor FLOOR says "not
/// before" and lets §848.1's spread choose the day, because sponsors trickle in as they sign. Tiers
/// are a handful of posts and he is naming when each ROUND happens — so these are §908 explicit
/// windows: the occurrence is placed from its own date and is deliberately NOT spread.</para>
/// </summary>
public sealed class SoMeTierRoundsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EventStart = new(2027, 2, 9, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int y, int m, int d) =>
        SoMeSchedulePlanner.ToUtc(new DateOnly(y, m, d), SoMeSchedulePlanner.PreferredTimes[0]);

    private static readonly DateTimeOffset Round1 = At(2026, 12, 15);
    private static readonly DateTimeOffset Round2 = At(2027, 1, 15);

    /// <summary>The four real ELDK27 tiers, announced twice each, with both rounds named.</summary>
    private static IReadOnlyList<PlannedSoMePost> PlanTiers(
        IReadOnlyDictionary<int, DateTimeOffset>? rounds)
    {
        var subjects = new[] { "Diamond", "Platinum", "Gold", "Silver" }
            .Select(t => new SoMeSubject(
                SoMeTemplateKind.SponsorCategory, $"tier:{t}", 2, EarliestByOccurrence: rounds))
            .ToList();

        return SoMeSchedulePlanner.Plan(
            subjects, Array.Empty<(string, int, DateTimeOffset)>(), Now, EventStart, normalPerDay: 2);
    }

    /// <summary>🔴 The reported ask, both halves.</summary>
    [Fact]
    public void Each_round_runs_from_its_own_date()
    {
        var plan = PlanTiers(new Dictionary<int, DateTimeOffset> { [1] = Round1, [2] = Round2 });

        var first = plan.Where(p => p.Occurrence == 1).ToList();
        var second = plan.Where(p => p.Occurrence == 2).ToList();

        Assert.Equal(4, first.Count);
        Assert.Equal(4, second.Count);
        Assert.All(first, p => Assert.True(p.ScheduledAtUtc >= Round1,
            $"{p.SubjectKey} round 1 landed {p.ScheduledAtUtc:dd-MM-yyyy}, before 15 Dec"));
        Assert.All(second, p => Assert.True(p.ScheduledAtUtc >= Round2,
            $"{p.SubjectKey} round 2 landed {p.ScheduledAtUtc:dd-MM-yyyy}, before 15 Jan"));
    }

    /// <summary>
    /// 🔒 A WINDOW, NOT A FLOOR — the round goes out TOGETHER rather than being spread over the
    /// months. This is the assertion that fails if someone turns these back into floors.
    /// </summary>
    [Fact]
    public void A_round_goes_out_together_rather_than_spread()
    {
        var plan = PlanTiers(new Dictionary<int, DateTimeOffset> { [1] = Round1, [2] = Round2 });

        var first = plan.Where(p => p.Occurrence == 1).Select(p => p.ScheduledAtUtc).ToList();
        var span = first.Max() - first.Min();

        // Four posts at 2/day is two weekdays. Allowing a week absorbs a weekend without admitting a
        // spread, which would scatter them across months.
        Assert.True(span <= TimeSpan.FromDays(7),
            $"round 1 spanned {span.TotalDays:0.#} days — that is a spread, not a run");
    }

    /// <summary>
    /// ⚠️ 15 December is eight days before the holiday closes. Round 1 must fit in front of it and
    /// never spill across — the two rules compose.
    /// </summary>
    [Fact]
    public void Round_one_does_not_spill_into_the_holiday_blackout()
    {
        var plan = PlanTiers(new Dictionary<int, DateTimeOffset> { [1] = Round1, [2] = Round2 });

        foreach (var p in plan)
        {
            var day = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(p.ScheduledAtUtc, SoMeSchedulePlanner.DanishTime).DateTime);
            Assert.False(SoMeBlackout.IsBlackedOut(day),
                $"{p.SubjectKey} #{p.Occurrence} landed on {day:dd-MM-yyyy}, inside the holiday blackout");
        }
    }

    /// <summary>
    /// 🔒 The two rounds stay APART. Round 2 is a reminder; landing it beside round 1 would announce
    /// the same tier twice in a week, which is why round 2 has no fallback to the round-1 date.
    /// </summary>
    [Fact]
    public void The_second_round_is_a_reminder_not_a_repeat()
    {
        var plan = PlanTiers(new Dictionary<int, DateTimeOffset> { [1] = Round1, [2] = Round2 });

        foreach (var key in plan.Select(p => p.SubjectKey).Distinct())
        {
            var one = plan.Single(p => p.SubjectKey == key && p.Occurrence == 1).ScheduledAtUtc;
            var two = plan.Single(p => p.SubjectKey == key && p.Occurrence == 2).ScheduledAtUtc;
            Assert.True(two - one > TimeSpan.FromDays(14),
                $"{key}'s two rounds are only {(two - one).TotalDays:0} days apart");
        }
    }

    /// <summary>
    /// ⚠️ With no rounds named, nothing changes — the ordinary spread, exactly as before §1181. A
    /// blank date is "no opinion", never "hold everything".
    /// </summary>
    [Fact]
    public void No_rounds_named_means_the_ordinary_spread()
    {
        var plan = PlanTiers(rounds: null);

        Assert.NotEmpty(plan);
        Assert.Contains(plan, p => p.ScheduledAtUtc < Round1);
    }

    /// <summary>Naming only round 1 leaves round 2 spreading — the fields are independent.</summary>
    [Fact]
    public void Round_one_alone_does_not_pin_round_two()
    {
        var plan = PlanTiers(new Dictionary<int, DateTimeOffset> { [1] = Round1 });

        Assert.All(plan.Where(p => p.Occurrence == 1),
            p => Assert.True(p.ScheduledAtUtc >= Round1));
        // Round 2 is free to sit wherever the spread puts it, including before round 1's window.
        Assert.Contains(plan, p => p.Occurrence == 2 && p.ScheduledAtUtc < Round1);
    }
}
