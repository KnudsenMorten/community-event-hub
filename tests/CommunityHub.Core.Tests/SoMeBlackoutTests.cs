using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1179 — NOBODY IS READING LINKEDIN BETWEEN CHRISTMAS AND NEW YEAR.
///
/// <para>Operator 2026-09-12, on auto-approval mails naming <i>"Wed 30 Dec 14:30"</i> and
/// <i>"Fri 01 Jan 11:00"</i>: <i>"dates are all over the place, even dec 30 and jan 1"</i> ·
/// <i>"this is not good"</i> · <i>"lets keep the current design, but make blockout between dec 23 -
/// jan 3 due to holidays"</i>.</para>
///
/// <para>🔑 <b>The spread was not malfunctioning — it had no idea holidays exist.</b> The planner's
/// only calendar rule was <c>IsWeekend</c>, and 30 December is a Wednesday. §848.3's own measured
/// table shows Dec 32 / Jan 31 posts, so the even spread put them there exactly as designed. This is
/// a gap in the calendar, not a reversal of the spread.</para>
/// </summary>
public sealed class SoMeBlackoutTests
{
    [Theory]
    // The two he actually saw.
    [InlineData(2026, 12, 30)]
    [InlineData(2027, 1, 1)]
    // Both ends INCLUSIVE — he named the first and last day people are away.
    [InlineData(2026, 12, 23)]
    [InlineData(2027, 1, 3)]
    // The middle, including the days that are not public holidays anywhere.
    [InlineData(2026, 12, 24)]
    [InlineData(2026, 12, 28)]
    [InlineData(2026, 12, 31)]
    public void The_holiday_window_is_blacked_out(int y, int m, int d) =>
        Assert.True(SoMeBlackout.IsBlackedOut(new DateOnly(y, m, d)));

    [Theory]
    // 🔒 The days either side are ORDINARY. An off-by-one here silently deletes two working days
    // from the campaign, which nobody would notice — an absence is invisible (§909).
    [InlineData(2026, 12, 22)]
    [InlineData(2027, 1, 4)]
    // Ordinary days in the same months, so the rule is a RANGE and not "December" or "January".
    [InlineData(2026, 12, 1)]
    [InlineData(2027, 1, 20)]
    [InlineData(2026, 9, 12)]
    public void Every_other_day_is_ordinary(int y, int m, int d) =>
        Assert.False(SoMeBlackout.IsBlackedOut(new DateOnly(y, m, d)));

    /// <summary>
    /// 🔒 EVERGREEN — the rule is month/day, never a fixed year. CEH is built so a new edition is a
    /// new Event row and a JSON config; a blackout pinned to 2026/2027 would quietly stop protecting
    /// the next edition, and the first anyone would know is a post going out on Christmas Eve.
    /// </summary>
    [Fact]
    public void It_protects_every_future_edition_not_just_this_one()
    {
        foreach (var year in new[] { 2027, 2028, 2031 })
        {
            Assert.True(SoMeBlackout.IsBlackedOut(new DateOnly(year, 12, 27)));
            Assert.True(SoMeBlackout.IsBlackedOut(new DateOnly(year, 1, 2)));
            Assert.False(SoMeBlackout.IsBlackedOut(new DateOnly(year, 12, 22)));
        }
    }

    /// <summary>⚠️ 2028 is a leap year — the range must not care.</summary>
    [Fact]
    public void A_leap_year_changes_nothing()
    {
        Assert.False(SoMeBlackout.IsBlackedOut(new DateOnly(2028, 2, 29)));
        Assert.True(SoMeBlackout.IsBlackedOut(new DateOnly(2028, 12, 25)));
    }

    /// <summary>The window REOPENS on 4 January, which is where displaced posts are packed.</summary>
    [Fact]
    public void The_next_allowed_day_is_the_fourth_of_January()
    {
        Assert.Equal(new DateOnly(2027, 1, 4), SoMeBlackout.NextAllowedDay(new DateOnly(2026, 12, 23)));
        Assert.Equal(new DateOnly(2027, 1, 4), SoMeBlackout.NextAllowedDay(new DateOnly(2026, 12, 30)));
        Assert.Equal(new DateOnly(2027, 1, 4), SoMeBlackout.NextAllowedDay(new DateOnly(2027, 1, 3)));
    }

    /// <summary>An ordinary day is its own answer — this must never move a date it has no quarrel with.</summary>
    [Fact]
    public void An_ordinary_day_is_returned_untouched()
    {
        var day = new DateOnly(2026, 10, 15);
        Assert.Equal(day, SoMeBlackout.NextAllowedDay(day));
    }

    /// <summary>
    /// 🔒 The planner places nothing inside the window. Asserted through the real placement path
    /// rather than by re-testing <see cref="SoMeBlackout"/>: the rule is only worth anything if the
    /// two loops that choose dates actually consult it.
    /// </summary>
    [Fact]
    public void The_planner_places_no_post_inside_the_window()
    {
        // A window that STRADDLES the holiday, so the only way to avoid it is to know about it.
        var from = new DateTimeOffset(2026, 12, 14, 0, 0, 0, TimeSpan.Zero);
        var eventStart = new DateTimeOffset(2027, 1, 20, 0, 0, 0, TimeSpan.Zero);

        // Enough subjects that a planner ignoring the holiday would certainly land in it.
        var subjects = Enumerable.Range(1, 30)
            .Select(i => new SoMeSubject(SoMeTemplateKind.Sponsor, $"sponsor:{i}", 1))
            .ToList();

        var plan = SoMeSchedulePlanner.Plan(
            subjects,
            Array.Empty<(string, int, DateTimeOffset)>(),
            from, eventStart, normalPerDay: 2);

        Assert.NotEmpty(plan);
        foreach (var p in plan)
        {
            var day = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(p.ScheduledAtUtc, SoMeSchedulePlanner.DanishTime).DateTime);
            Assert.False(SoMeBlackout.IsBlackedOut(day),
                $"post for {p.SubjectKey} was placed on {day:dd-MM-yyyy}, inside the holiday blackout");
            Assert.False(day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                $"post for {p.SubjectKey} was placed on a weekend ({day:dd-MM-yyyy})");
        }
    }

    /// <summary>
    /// ⚠️ And the RE-TIME path too, which is the one that moves posts that already exist — a fix
    /// that only covered new posts would leave the 30 December post exactly where it is.
    /// </summary>
    [Fact]
    public void A_retime_never_lands_on_the_holiday_either()
    {
        var movable = Enumerable.Range(1, 8)
            .Select(i => (PostId: i, SubjectKey: $"session:{i}",
                          ScheduledAtUtc: new DateTimeOffset(2026, 12, 28, 10, 0, 0, TimeSpan.Zero)))
            .ToList();

        var moves = SoMeSchedulePlanner.RetimeIntoWindow(
            movable,
            Array.Empty<DateTimeOffset>(),
            new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 2, 9, 0, 0, 0, TimeSpan.Zero),
            maxPerDay: 2);

        Assert.NotEmpty(moves);
        foreach (var (_, at) in moves)
        {
            var day = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(at, SoMeSchedulePlanner.DanishTime).DateTime);
            Assert.False(SoMeBlackout.IsBlackedOut(day),
                $"a re-time landed on {day:dd-MM-yyyy}, inside the holiday blackout");
        }
    }
}
