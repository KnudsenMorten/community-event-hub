using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1179 — SPONSORS DO NOT START BEFORE A DATE HE SETS.
///
/// <para>Operator 2026-09-12: <i>"i wants sponsors to start from oct 15"</i>, immediately after
/// <i>"lets keep the current design"</i>.</para>
///
/// <para>🔒 <b>A FLOOR, not a window — and that follows from "keep the current design".</b> A window
/// (<c>MasterClassAnnouncementFrom</c>) packs its posts forward from the date and overrules the
/// spread; a floor says only "not before this" and lets §848.1's spread go on choosing the day.
/// Building it as a window would have bunched every sponsor into one week, which is the exact
/// front-loading §848.1 exists to prevent.</para>
/// </summary>
public sealed class SoMeSponsorFloorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset EventStart =
        new(2027, 2, 9, 0, 0, 0, TimeSpan.Zero);

    /// <summary>15 October 2026 at the first preferred time, the way the service builds it.</summary>
    private static DateTimeOffset Floor =>
        SoMeSchedulePlanner.ToUtc(new DateOnly(2026, 10, 15), SoMeSchedulePlanner.PreferredTimes[0]);

    /// <summary>
    /// Sponsors whose artwork has been ready since AUGUST — the founding backlog — planned the way
    /// <c>CollectSubjectsAsync</c> builds them.
    /// </summary>
    /// <remarks>
    /// 🔑 <c>PromptFromUtc</c> is the sponsor's OWN readiness and is deliberately NOT raised to the
    /// floor. §851: *"a type-wide floor spreads; an individual arrival hurries"* — passing the floor
    /// here would tell the planner all twelve just became ready, and they would hurry instead of
    /// spreading. That was the bug these tests caught.
    /// </remarks>
    private static IReadOnlyList<PlannedSoMePost> PlanSponsors(DateTimeOffset? floor)
    {
        var readySince = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

        var subjects = Enumerable.Range(1, 12)
            .Select(i => new SoMeSubject(
                SoMeTemplateKind.Sponsor, $"sponsor:{i}", 2,
                EarliestUtc: floor is null ? readySince : (readySince > floor ? readySince : floor),
                PromptFromUtc: readySince))
            .ToList();

        return SoMeSchedulePlanner.Plan(
            subjects, Array.Empty<(string, int, DateTimeOffset)>(), Now, EventStart, normalPerDay: 2);
    }

    /// <summary>🔴 The reported ask: nothing sponsor-shaped lands before the date.</summary>
    [Fact]
    public void No_sponsor_post_is_placed_before_the_floor()
    {
        var plan = PlanSponsors(Floor);

        Assert.NotEmpty(plan);
        foreach (var p in plan)
        {
            Assert.True(p.ScheduledAtUtc >= Floor,
                $"{p.SubjectKey} #{p.Occurrence} was placed {p.ScheduledAtUtc:dd-MM-yyyy HH:mm}, "
                + $"before the {Floor:dd-MM-yyyy} floor");
        }
    }

    /// <summary>
    /// 🔒 IT IS A FLOOR, NOT A WINDOW — the posts still SPREAD after it rather than packing into the
    /// first few days. This is the assertion that would fail if someone "improved" it into a window.
    /// </summary>
    [Fact]
    public void Sponsors_still_spread_across_the_run_up_after_the_floor()
    {
        var plan = PlanSponsors(Floor);

        var months = plan
            .Select(p => (p.ScheduledAtUtc.Year, p.ScheduledAtUtc.Month))
            .Distinct()
            .Count();

        // 24 posts at 2/day would fit in a fortnight if they packed. Spreading them from mid-October
        // to February must touch several months.
        Assert.True(months >= 3,
            $"sponsor posts landed in only {months} month(s) — that is a window, not a floor");
    }

    /// <summary>
    /// ⚠️ With no floor set, nothing changes: a blank date is "no opinion", not "hold everything".
    /// </summary>
    [Fact]
    public void No_floor_means_the_ordinary_spread()
    {
        var plan = PlanSponsors(floor: null);

        Assert.NotEmpty(plan);
        Assert.Contains(plan, p => p.ScheduledAtUtc < Floor);
    }

    /// <summary>
    /// 🔑 THE FLOOR CAN ONLY DELAY, NEVER HURRY. A sponsor whose artwork is not ready until November
    /// is not dragged back to 15 October — the later of the two "not before" dates wins, which is the
    /// same rule §920 applies to tracks.
    /// </summary>
    [Fact]
    public void A_sponsor_ready_later_than_the_floor_keeps_its_own_later_date()
    {
        var readyInNovember = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
        var later = readyInNovember > Floor ? readyInNovember : Floor;

        var plan = SoMeSchedulePlanner.Plan(
            [new SoMeSubject(SoMeTemplateKind.Sponsor, "sponsor:99", 1,
                EarliestUtc: later, PromptFromUtc: later)],
            Array.Empty<(string, int, DateTimeOffset)>(),
            Now, EventStart, normalPerDay: 2);

        var post = Assert.Single(plan);
        Assert.True(post.ScheduledAtUtc >= readyInNovember,
            "the floor must never pull a sponsor forward past their own readiness");
    }

    /// <summary>
    /// ⚠️ And it must not land on the holidays either — the two rules compose, since a floor in
    /// mid-October pushes some of the second round straight into December.
    /// </summary>
    [Fact]
    public void The_floor_and_the_holiday_blackout_both_apply()
    {
        foreach (var p in PlanSponsors(Floor))
        {
            var day = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(p.ScheduledAtUtc, SoMeSchedulePlanner.DanishTime).DateTime);
            Assert.False(SoMeBlackout.IsBlackedOut(day),
                $"{p.SubjectKey} #{p.Occurrence} landed on {day:dd-MM-yyyy}, inside the holiday blackout");
        }
    }
}
