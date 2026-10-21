using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §1297 — the TASK side of the time model: the typed window, and the parser that keeps it
/// working for rows nothing has re-saved.
/// </summary>
public sealed class VolunteerTaskWindowTests
{
    [Theory]
    [InlineData("08:00", 8, 0)]
    [InlineData("8:00", 8, 0)]
    [InlineData("06:40", 6, 40)]
    [InlineData("17:30", 17, 30)]
    [InlineData("21:00", 21, 0)]
    public void Parses_the_clean_HH_mm_the_plan_actually_carries(string text, int h, int m) =>
        Assert.Equal(new TimeOnly(h, m), VolunteerTaskWindow.ParseTime(text));

    [Theory]
    [InlineData("Day 1, 08:00–10:00", 8, 0)]   // the documented free-text shape
    [InlineData("kl. 8.00", 8, 0)]             // dot separator
    public void Finds_the_first_clock_time_inside_free_text(string text, int h, int m) =>
        Assert.Equal(new TimeOnly(h, m), VolunteerTaskWindow.ParseTime(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("morning")]
    [InlineData("25:00")]   // not a clock time
    public void Returns_null_rather_than_inventing_a_time(string? text) =>
        Assert.Null(VolunteerTaskWindow.ParseTime(text));

    [Fact]
    public void The_typed_columns_are_preferred_when_present()
    {
        var t = new VolunteerTask
        {
            Shift = "ignored", TimeEnd = "ignored",
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(12, 0),
        };

        var w = VolunteerTaskWindow.WindowOf(t);
        Assert.Equal(new TimeWindow(new TimeOnly(9, 0), new TimeOnly(12, 0)), w);
    }

    [Fact]
    public void It_FALLS_BACK_to_the_text_when_the_typed_columns_are_empty()
    {
        // 🔑 The reason the parser survives alongside the columns: a task that arrives from an
        // import path we have not touched, or from another edition, still schedules instead of
        // silently dropping out of the plan.
        var t = new VolunteerTask { Shift = "08:00", TimeEnd = "15:00" };

        var w = VolunteerTaskWindow.WindowOf(t);
        Assert.Equal(new TimeWindow(new TimeOnly(8, 0), new TimeOnly(15, 0)), w);
        Assert.Equal(7d, w!.Value.Hours);
    }

    [Fact]
    public void A_task_with_no_usable_pair_has_no_window()
    {
        Assert.Null(VolunteerTaskWindow.WindowOf(new VolunteerTask { Shift = "08:00" }));            // no end
        Assert.Null(VolunteerTaskWindow.WindowOf(new VolunteerTask { TimeEnd = "15:00" }));          // no start
        Assert.Null(VolunteerTaskWindow.WindowOf(new VolunteerTask()));                              // neither
        Assert.Null(VolunteerTaskWindow.WindowOf(
            new VolunteerTask { Shift = "15:00", TimeEnd = "09:00" }));                              // end before start
    }

    [Fact]
    public void SyncTypedTimes_projects_the_text_and_clears_a_stale_value()
    {
        var t = new VolunteerTask
        {
            Shift = "09:00", TimeEnd = "11:00",
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(15, 0),   // stale
        };

        VolunteerTaskWindow.SyncTypedTimes(t);

        Assert.Equal(new TimeOnly(9, 0), t.StartTime);
        Assert.Equal(new TimeOnly(11, 0), t.EndTime);

        // ⚠️ And clearing the text must clear the projection — otherwise a task whose hours were
        // deliberately removed would keep being scheduled against the old ones.
        t.Shift = null;
        t.TimeEnd = null;
        VolunteerTaskWindow.SyncTypedTimes(t);

        Assert.Null(t.StartTime);
        Assert.Null(t.EndTime);
    }
}
