using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1368 — THE PLANNING RULES HE CAN SET, AND THE MAIL WHEN ONE IS BROKEN.
///
/// <para>Operator 2026-10-26: <i>"all sponsors first post must be no later than dec 15 on linkedin"</i>
/// · <i>"we need to be able to publish them 2 times with enough time between so they dont popup close
/// to each other"</i> · <i>"we need to have an email if conflicts happens that breaks rules"</i>.
/// Measured that day: Egiss and 2Pint both planned 3 + 5 February — two days apart, both after the
/// date he wanted every first post out by.</para>
///
/// <para>🔒 Every rule here is honoured when a seat exists and RELAXED when none does — a sponsor post
/// is never dropped (§842.5). The conflict check is what tells him a rule was relaxed.</para>
/// </summary>
public sealed class SoMePlanningRuleConflictTests
{
    private static readonly DateTimeOffset From = SoMeSchedulePlanner.ToUtc(new DateOnly(2026, 11, 2), new TimeOnly(7, 0));
    private static readonly DateTimeOffset EventStart = new(2027, 2, 9, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Dec15 = new(2026, 12, 15);

    private static DateOnly Day(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, SoMeSchedulePlanner.DanishTime).DateTime);

    /// <summary>Every weekday from <paramref name="from"/> to <paramref name="to"/>, filled to <paramref name="perDay"/>.</summary>
    private static List<(string, int, DateTimeOffset)> Fill(DateOnly from, DateOnly to, int perDay)
    {
        var rows = new List<(string, int, DateTimeOffset)>();
        var n = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            for (var i = 0; i < perDay; i++)
                rows.Add(($"event:filler-{n++}", 1, SoMeSchedulePlanner.ToUtc(d, SoMeSchedulePlanner.PreferredTimes[i])));
        }
        return rows;
    }

    // --- the planner ---------------------------------------------------------------------------

    /// <summary>🔴 The Egiss case: round 2 opens the day after round 1, and a minimum keeps them apart.</summary>
    [Fact]
    public void A_subjects_two_rounds_keep_the_minimum_gap_and_their_order()
    {
        var round2Opens = new Dictionary<int, DateTimeOffset> { [2] = From.AddDays(1) };
        var loose = new SoMeSubject(SoMeTemplateKind.Sponsor, "sponsor:egiss", 2, EarliestByOccurrence: round2Opens);
        var spaced = loose with { MinDaysBetweenOccurrences = 14 };

        var control = SoMeSchedulePlanner.Plan([loose], [], From, EventStart);
        var plan = SoMeSchedulePlanner.Plan([spaced], [], From, EventStart);

        int Gap(IReadOnlyList<PlannedSoMePost> p) =>
            Day(p.Single(x => x.Occurrence == 2).ScheduledAtUtc).DayNumber
            - Day(p.Single(x => x.Occurrence == 1).ScheduledAtUtc).DayNumber;

        // ⚠️ The control proves the test exercises the rule: without it the two land close.
        Assert.True(Gap(control) < 14, $"control gap {Gap(control)}");
        Assert.True(Gap(plan) >= 14, $"gap {Gap(plan)}");
    }

    /// <summary>🔴 "First post by 15 December": a full calendar gets a third post that day rather than miss it.</summary>
    [Fact]
    public void A_round_deadline_is_met_with_a_busy_day_before_it_is_missed()
    {
        var full = Fill(new DateOnly(2026, 11, 2), Dec15, perDay: 2);
        var free = new SoMeSubject(SoMeTemplateKind.Sponsor, "sponsor:late", 1);
        var dated = free with
        {
            LatestByOccurrence = new Dictionary<int, DateTimeOffset> { [1] = SoMeCategoryRules.PublishByUtc(Dec15) },
        };

        var control = SoMeSchedulePlanner.Plan([free], full, From, EventStart, 2, exceptionPerDay: 3);
        var plan = SoMeSchedulePlanner.Plan([dated], full, From, EventStart, 2, exceptionPerDay: 3);

        Assert.True(Day(control.Single().ScheduledAtUtc) > Dec15);
        Assert.True(Day(plan.Single().ScheduledAtUtc) <= Dec15);
    }

    /// <summary>🔒 A deadline that cannot be met still places the post — late, and reported, never dropped.</summary>
    [Fact]
    public void A_deadline_that_cannot_be_met_still_places_the_post()
    {
        var full = Fill(new DateOnly(2026, 11, 2), Dec15, perDay: 3);
        var dated = new SoMeSubject(SoMeTemplateKind.Sponsor, "sponsor:signs-late", 1,
            LatestByOccurrence: new Dictionary<int, DateTimeOffset> { [1] = SoMeCategoryRules.PublishByUtc(Dec15) });

        var plan = SoMeSchedulePlanner.Plan([dated], full, From, EventStart, 2, exceptionPerDay: 3);

        Assert.True(Day(Assert.Single(plan).ScheduledAtUtc) > Dec15);
    }

    /// <summary>🔑 One sponsor a day: two sponsors are kept on different days while a seat allows it.</summary>
    [Fact]
    public void The_sponsor_per_day_limit_keeps_two_sponsors_on_different_days()
    {
        // One week, every day full except Wednesday (2 free) and Thursday (1 free).
        var week = Fill(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 6), perDay: 2)
            .Where(r => Day(r.Item3) != new DateOnly(2026, 11, 4)
                        && !(Day(r.Item3) == new DateOnly(2026, 11, 5) && r.Item3.Hour > 9))
            .ToList();
        var end = new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);
        var subjects = new[]
        {
            new SoMeSubject(SoMeTemplateKind.Sponsor, "sponsor:a", 1),
            new SoMeSubject(SoMeTemplateKind.Sponsor, "sponsor:b", 1),
        };

        var plan = SoMeSchedulePlanner.Plan(subjects, week, From, end, 2, maxSponsorPerDay: 1);

        Assert.Equal(2, plan.Count);
        Assert.NotEqual(Day(plan[0].ScheduledAtUtc), Day(plan[1].ScheduledAtUtc));
    }

    // --- the conflict check ----------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static SoMeConflictPost Post(int id, string key, int occ, DateOnly day, int hour = 9, bool movable = true) =>
        new(id, key, occ, SoMeAnnouncementCategory.Sponsors,
            SoMeSchedulePlanner.ToUtc(day, new TimeOnly(hour, 0)), movable, key);

    private static SoMeConflictRules Rules(int minDays = 0, DateOnly? round1By = null, int? sponsorPerDay = null, int perDay = 3) => new(
        minDays > 0
            ? new Dictionary<SoMeAnnouncementCategory, int> { [SoMeAnnouncementCategory.Sponsors] = minDays }
            : new Dictionary<SoMeAnnouncementCategory, int>(),
        round1By is { } by
            ? new Dictionary<SoMeAnnouncementCategory, IReadOnlyDictionary<int, DateOnly>>
              { [SoMeAnnouncementCategory.Sponsors] = new Dictionary<int, DateOnly> { [1] = by } }
            : new Dictionary<SoMeAnnouncementCategory, IReadOnlyDictionary<int, DateOnly>>(),
        sponsorPerDay, perDay);

    [Fact]
    public void A_first_post_after_its_publish_by_date_is_a_conflict_and_a_published_one_is_not()
    {
        var late = Post(1, "sponsor:egiss", 1, new DateOnly(2027, 2, 3));
        var gone = Post(2, "sponsor:old", 1, new DateOnly(2027, 1, 5), movable: false);

        var conflicts = SoMeRuleConflictService.Find([late, gone], Rules(round1By: Dec15), Now);

        var c = Assert.Single(conflicts);
        Assert.Equal("Publish-by date", c.Rule);
        Assert.Equal(new[] { 1 }, c.PostIds);
    }

    [Fact]
    public void Rounds_too_close_together_or_in_the_wrong_order_are_conflicts()
    {
        var close = new[]
        {
            Post(1, "sponsor:egiss", 1, new DateOnly(2027, 2, 3)),
            Post(2, "sponsor:egiss", 2, new DateOnly(2027, 2, 5)),
        };
        var swapped = new[]
        {
            Post(3, "sponsor:twopint", 1, new DateOnly(2027, 1, 20)),
            Post(4, "sponsor:twopint", 2, new DateOnly(2027, 1, 10)),
        };
        var fine = new[]
        {
            Post(5, "sponsor:ok", 1, new DateOnly(2026, 11, 10)),
            Post(6, "sponsor:ok", 2, new DateOnly(2027, 1, 10)),
        };

        var conflicts = SoMeRuleConflictService.Find([.. close, .. swapped, .. fine], Rules(minDays: 14), Now);

        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, c => c.PostIds.SequenceEqual([1, 2]) && c.Message.Contains("only 2 day(s) apart"));
        Assert.Contains(conflicts, c => c.PostIds.SequenceEqual([3, 4]) && c.Message.Contains("BEFORE"));
    }

    [Fact]
    public void Too_many_sponsors_or_posts_on_one_day_and_unplaceable_posts_are_conflicts()
    {
        var day = new DateOnly(2026, 11, 12);
        var posts = new[]
        {
            Post(1, "sponsor:a", 1, day, 9),
            Post(2, "sponsor:b", 1, day, 11),
            Post(3, "event:x", 1, day, 13) with { Category = SoMeAnnouncementCategory.EventPosts },
            Post(4, "event:y", 1, day, 14) with { Category = SoMeAnnouncementCategory.EventPosts },
        };

        var conflicts = SoMeRuleConflictService.Find(posts, Rules(sponsorPerDay: 1, perDay: 3), Now,
            noRoom: ["sponsor:c #1"]);

        Assert.Contains(conflicts, c => c.Rule == "Sponsor posts per day" && c.PostIds.SequenceEqual([1, 2]));
        Assert.Contains(conflicts, c => c.Rule == "Posts per day" && c.PostIds.Count == 4);
        Assert.Contains(conflicts, c => c.Rule == "No room before the event" && c.Message.StartsWith("sponsor:c #1:"));
    }

    [Fact]
    public void A_queue_that_keeps_every_rule_has_no_conflicts()
    {
        var posts = new[]
        {
            Post(1, "sponsor:a", 1, new DateOnly(2026, 11, 10)),
            Post(2, "sponsor:a", 2, new DateOnly(2027, 1, 12)),
            Post(3, "sponsor:b", 1, new DateOnly(2026, 11, 11)),
        };

        Assert.Empty(SoMeRuleConflictService.Find(posts, Rules(minDays: 14, round1By: Dec15, sponsorPerDay: 1), Now));
    }

    // --- the settings ----------------------------------------------------------------------------

    /// <summary>🔒 The page owns the new values; a caller that does not say so cannot wipe them.</summary>
    [Fact]
    public async Task Deadlines_and_spacing_are_saved_and_survive_a_save_that_does_not_own_them()
    {
        using var db = new CommunityHubDbContext(
            new DbContextOptionsBuilder<CommunityHubDbContext>()
                .UseInMemoryDatabase($"some-1368-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event
        {
            Id = 1, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            VenueName = "Bella Center", StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();

        var svc = new SoMeCategoryRules(db);
        var cat = SoMeAnnouncementCategory.Sponsors;
        var noStarts = new Dictionary<int, DateOnly?>();

        await svc.SaveAsync(1, cat, true, 2, null, noStarts, "organizer@example.test",
            roundDeadlines: new Dictionary<int, DateOnly?> { [1] = Dec15 },
            minDaysBetweenRounds: 14, updateSpacingRules: true);

        await svc.SaveAsync(1, cat, true, 2, null, noStarts, "organizer@example.test");

        Assert.Equal(14, (await svc.GetAllAsync(1))[cat].MinDaysBetweenRounds);
        Assert.Equal(Dec15, (await svc.RoundDeadlinesAsync(1))[cat][1]);
        // A deadline alone is not a start date: round 1 still opens "when each sponsor is ready".
        Assert.False((await svc.RoundStartsAsync(1)).ContainsKey(cat));
    }
}
