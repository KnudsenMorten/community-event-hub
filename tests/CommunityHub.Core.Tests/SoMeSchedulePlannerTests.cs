using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §824.2E — when each post goes out.
/// </summary>
/// <remarks>
/// 🔒 The property that matters most is <b>stability</b>. The scheduler runs on a timer, so it
/// re-plans constantly; with real randomness every run would move every unpublished post to a new
/// day, the queue would never settle, and a post he approved yesterday would be somewhere else today.
/// Several tests below exist only to hold that.
/// </remarks>
public sealed class SoMeSchedulePlannerTests
{
    private static readonly DateTimeOffset From = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);   // Wed
    private static readonly DateTimeOffset EventStart = new(2027, 2, 9, 8, 0, 0, TimeSpan.Zero);

    private static readonly (string, int, DateTimeOffset)[] None = Array.Empty<(string, int, DateTimeOffset)>();

    private static SoMeSubject Sponsor(string id, int times = 2) =>
        new(SoMeTemplateKind.Sponsor, id, times);

    [Fact]
    public void Every_post_lands_on_a_weekday_at_one_of_his_two_times()
    {
        var subjects = Enumerable.Range(1, 25).Select(i => Sponsor($"co-{i}")).ToList();

        var plan = SoMeSchedulePlanner.Plan(subjects, None, From, EventStart);

        Assert.NotEmpty(plan);
        foreach (var p in plan)
        {
            var local = TimeZoneInfo.ConvertTime(p.ScheduledAtUtc, SoMeSchedulePlanner.DanishTime);
            Assert.NotEqual(DayOfWeek.Saturday, local.DayOfWeek);
            Assert.NotEqual(DayOfWeek.Sunday, local.DayOfWeek);

            var t = TimeOnly.FromTimeSpan(local.TimeOfDay);
            Assert.Contains(t, SoMeSchedulePlanner.PreferredTimes);
            // The guard rail he actually described: never 22:15.
            Assert.InRange(t, SoMeSchedulePlanner.EarliestTime, SoMeSchedulePlanner.LatestTime);
        }
    }

    [Fact]
    public void Re_planning_produces_the_identical_schedule()
    {
        // 🔒 THE ONE THAT PROTECTS THE QUEUE. A timer re-runs this; the answer must not move.
        var subjects = Enumerable.Range(1, 12).Select(i => Sponsor($"co-{i}")).ToList();

        var a = SoMeSchedulePlanner.Plan(subjects, None, From, EventStart);
        var b = SoMeSchedulePlanner.Plan(subjects, None, From, EventStart);

        Assert.Equal(
            a.Select(p => (p.SubjectKey, p.Occurrence, p.ScheduledAtUtc)),
            b.Select(p => (p.SubjectKey, p.Occurrence, p.ScheduledAtUtc)));
    }

    [Fact]
    public void Something_already_queued_is_never_planned_again()
    {
        var subjects = new[] { Sponsor("co-1"), Sponsor("co-2") };
        var existing = new[]
        {
            ("co-1", 1, From.AddDays(1)),
            ("co-2", 2, From.AddDays(2)),
        };

        var plan = SoMeSchedulePlanner.Plan(subjects, existing, From, EventStart);

        Assert.Equal(2, plan.Count);
        Assert.DoesNotContain(plan, p => p.SubjectKey == "co-1" && p.Occurrence == 1);
        Assert.DoesNotContain(plan, p => p.SubjectKey == "co-2" && p.Occurrence == 2);
    }

    [Fact]
    public void A_new_sponsor_is_placed_AROUND_the_queue_never_on_top_of_it()
    {
        // 🔒 THE REGRESSION THAT CHANGED THE DESIGN. The first version took only the KEYS of existing
        // posts, so it correctly skipped re-planning them — and had no idea WHEN they were, which
        // let a new arrival land on the same instant as one already queued and approved.
        //
        // This is the real production flow: tick 1 plans and persists, tick 2 sees a new sponsor.
        var firstPass = SoMeSchedulePlanner.Plan(
            new[] { Sponsor("co-1"), Sponsor("co-2") }, None, From, EventStart);

        var persisted = firstPass
            .Select(p => (p.SubjectKey, p.Occurrence, p.ScheduledAtUtc))
            .ToArray();

        var secondPass = SoMeSchedulePlanner.Plan(
            new[] { Sponsor("co-1"), Sponsor("co-2"), Sponsor("co-NEW") },
            persisted, From, EventStart);

        // Only the newcomer is planned — the queue he has already read is untouched by construction.
        Assert.All(secondPass, p => Assert.Equal("co-NEW", p.SubjectKey));
        Assert.Equal(2, secondPass.Count);

        // …and it does not collide with anything already in the queue.
        Assert.Empty(secondPass.Select(p => p.ScheduledAtUtc)
            .Intersect(persisted.Select(e => e.ScheduledAtUtc)));
    }

    [Fact]
    public void A_day_that_is_already_full_is_not_filled_further()
    {
        // Two posts already sit on the first available weekday; the newcomer must go elsewhere.
        var day = SoMeSchedulePlanner.ToUtc(new DateOnly(2026, 8, 6), new TimeOnly(11, 0));
        var day2 = SoMeSchedulePlanner.ToUtc(new DateOnly(2026, 8, 6), new TimeOnly(14, 30));
        var existing = new[] { ("old-1", 1, day), ("old-2", 1, day2) };

        var plan = SoMeSchedulePlanner.Plan(
            new[] { Sponsor("co-NEW", 1) }, existing, From, EventStart);

        var placed = Assert.Single(plan);
        var local = TimeZoneInfo.ConvertTime(placed.ScheduledAtUtc, SoMeSchedulePlanner.DanishTime);
        Assert.NotEqual(new DateOnly(2026, 8, 6), DateOnly.FromDateTime(local.Date));
    }

    [Fact]
    public void Two_posts_never_share_a_slot_and_a_day_holds_at_most_two()
    {
        var subjects = Enumerable.Range(1, 40).Select(i => Sponsor($"co-{i}", 1)).ToList();

        var plan = SoMeSchedulePlanner.Plan(subjects, None, From, EventStart);

        Assert.Equal(plan.Count, plan.Select(p => p.ScheduledAtUtc).Distinct().Count());

        var perDay = plan
            .GroupBy(p => TimeZoneInfo.ConvertTime(p.ScheduledAtUtc, SoMeSchedulePlanner.DanishTime).Date)
            .Select(g => g.Count());
        Assert.All(perDay, n => Assert.True(n <= 2, $"{n} posts on one day"));
    }

    [Fact]
    public void Nothing_is_scheduled_before_now_or_on_the_event_itself()
    {
        var subjects = Enumerable.Range(1, 15).Select(i => Sponsor($"co-{i}")).ToList();

        var plan = SoMeSchedulePlanner.Plan(subjects, None, From, EventStart);

        Assert.All(plan, p => Assert.True(p.ScheduledAtUtc >= From));
        Assert.All(plan, p => Assert.True(p.ScheduledAtUtc < EventStart));
    }

    [Fact]
    public void A_subject_that_is_not_ready_yet_waits()
    {
        // Type 2's sponsor-speaker sessions: "when available, depends on when sponsor has assigned a
        // person to their session". Until then there is nothing to announce.
        var ready = From.AddMonths(3);
        var subjects = new[]
        {
            new SoMeSubject(SoMeTemplateKind.Session, "sess-1", 2, EarliestUtc: ready),
        };

        var plan = SoMeSchedulePlanner.Plan(subjects, None, From, EventStart);

        Assert.NotEmpty(plan);
        Assert.All(plan, p => Assert.True(p.ScheduledAtUtc >= ready));
    }

    [Fact]
    public void No_room_before_the_event_means_no_post_rather_than_a_post_after_it()
    {
        // ⚠️ Announcing a session the day after the event is worse than not announcing it. The
        // planner returns fewer posts and lets the caller report the shortfall.
        var tightEvent = From.AddDays(3);           // Wed → Sat: two weekdays, 4 slots
        var subjects = Enumerable.Range(1, 20).Select(i => Sponsor($"co-{i}", 1)).ToList();

        var plan = SoMeSchedulePlanner.Plan(subjects, None, From, tightEvent);

        Assert.All(plan, p => Assert.True(p.ScheduledAtUtc < tightEvent));
        Assert.True(plan.Count < subjects.Count, "should not have squeezed everything in");
    }

    [Fact]
    public void The_hash_is_stable_across_processes()
    {
        // 🔒 string.GetHashCode() is randomised per process in .NET, so using it would move every
        // post on every app restart — the exact reshuffle this planner exists to prevent. Pinning a
        // literal value is what catches someone "simplifying" it back.
        Assert.Equal(SoMeSchedulePlanner.StableHash("co-1|1"), SoMeSchedulePlanner.StableHash("co-1|1"));
        Assert.NotEqual(SoMeSchedulePlanner.StableHash("co-1|1"), SoMeSchedulePlanner.StableHash("co-1|2"));
        Assert.Equal(4001427846u, SoMeSchedulePlanner.StableHash("co-1|1"));
    }

    [Fact]
    public void Every_preferred_time_actually_gets_used()
    {
        // If everything landed at 11:00 the "spread across the preferred times" rule would be
        // decoration. §842.7 added a THIRD time (09:00) so maxPerDay could usefully go to 3 —
        // operator 2026-08-05: "we have had 3 pr day some days at prior events". Asserted against
        // the array's own length rather than a literal, so a fourth time cannot leave this stale.
        var subjects = Enumerable.Range(1, 30).Select(i => Sponsor($"co-{i}", 1)).ToList();

        var plan = SoMeSchedulePlanner.Plan(subjects, None, From, EventStart);

        var times = plan
            .Select(p => TimeOnly.FromTimeSpan(
                TimeZoneInfo.ConvertTime(p.ScheduledAtUtc, SoMeSchedulePlanner.DanishTime).TimeOfDay))
            .Distinct().ToList();

        Assert.Equal(SoMeSchedulePlanner.PreferredTimes.Length, times.Count);

        // 🔒 And every one of them stays inside his 08:00–16:00 guard rail.
        Assert.All(times, t => Assert.InRange(
            t, SoMeSchedulePlanner.EarliestTime, SoMeSchedulePlanner.LatestTime));
    }

    [Fact]
    public void A_local_time_that_does_not_exist_on_a_DST_night_still_produces_a_slot()
    {
        // Denmark springs forward at 02:00, so 11:00 is never invalid — but the guard is here so a
        // future timezone or time change cannot take a whole planning run down for one unlucky day.
        var utc = SoMeSchedulePlanner.ToUtc(new DateOnly(2027, 3, 28), new TimeOnly(11, 0));

        var local = TimeZoneInfo.ConvertTime(utc, SoMeSchedulePlanner.DanishTime);
        Assert.Equal(new DateOnly(2027, 3, 28), DateOnly.FromDateTime(local.Date));
    }
}
