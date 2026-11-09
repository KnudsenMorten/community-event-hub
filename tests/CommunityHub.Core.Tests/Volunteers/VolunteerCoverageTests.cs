using CommunityHub.Core.Volunteers;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>§1380 — the coverage &amp; shortage overview (pure).</summary>
public sealed class VolunteerCoverageTests
{
    private static readonly DateOnly Day = new(2027, 2, 10);
    private static TimeWindow W(int h1, int h2) => new(new TimeOnly(h1, 0), new TimeOnly(h2, 0));
    private static CoverageAnswer Here(int pid, TimeWindow? w, bool worker = false) => new(pid, Day, new DayWindow(true, w), worker);

    [Fact]
    public void Hourly_head_count_shortage_names_the_busiest_hour()
    {
        // 08–10 needs 3 people; only 2 said they are here at 08 ⇒ short 1 at 08:00–09:00 and 09:00–10:00.
        var r = VolunteerCoverage.Compute(
            new[] { new CoverageTask(1, 3, 0, Day, W(8, 10)) },
            new[] { Here(1, W(7, 17)), Here(2, W(7, 17)), Here(3, W(12, 17)) });

        var d = Assert.Single(r.Days);
        Assert.Equal(2, d.Hours.Count);
        Assert.Equal(1, d.Peak!.ShortOfPeople);
        Assert.Equal("08:00–09:00", d.Peak.Label);
        Assert.Equal(3, d.OpenSlots);
    }

    [Fact]
    public void Person_hour_gap_uses_the_50_percent_rule_and_full_day_workers()
    {
        // Needs 2 × 8h = 16h. Two attendees with 10h stated give 5h each = 10h ⇒ short 6h ≈ 2 half-days.
        var tasks = new[] { new CoverageTask(1, 2, 0, Day, W(8, 16)) };
        var r = VolunteerCoverage.Compute(tasks, new[] { Here(1, W(7, 17)), Here(2, W(7, 17)) });
        var d = Assert.Single(r.Days);
        Assert.Equal(16d, d.NeededHours);
        Assert.Equal(10d, d.PlannableHours);
        Assert.Equal(6d, d.GapHours);
        Assert.Equal(2, d.ExtraVolunteersAtHalfDay);
        Assert.Null(d.Peak);   // the people ARE here at every hour — they are capped, not missing

        // Mark one as a full-day worker: 10 + 5 = 15h ⇒ short 1h.
        var r2 = VolunteerCoverage.Compute(tasks, new[] { Here(1, W(7, 17), worker: true), Here(2, W(7, 17)) });
        Assert.Equal(1d, r2.Days[0].GapHours);
        Assert.Equal(1, r2.Days[0].FullDayWorkersPresent);
    }

    [Fact]
    public void Filled_never_exceeds_needed_and_undated_tasks_are_counted_apart()
    {
        var r = VolunteerCoverage.Compute(
            new[]
            {
                new CoverageTask(1, 2, 5, Day, W(9, 10)),    // over-filled: counts as 2
                new CoverageTask(2, 3, 1, null, null),       // undated
            },
            Array.Empty<CoverageAnswer>());

        Assert.Equal(2, r.Days[0].FilledSlots);
        Assert.Equal(0, r.Days[0].OpenSlots);
        Assert.Equal(1, r.UndatedTasks);
        Assert.Equal(3, r.UndatedSlots);
        Assert.Equal(2, r.UndatedOpen);
        Assert.Equal(5, r.NeededSlots);
        Assert.Equal(2, r.OpenSlots);
    }

    [Fact]
    public void A_present_answer_without_hours_counts_as_available_all_day()
    {
        var r = VolunteerCoverage.Compute(
            new[] { new CoverageTask(1, 1, 0, Day, W(20, 21)) },
            new[] { Here(1, null) });
        Assert.Equal(1, r.Days[0].Hours.Single().Available);
        Assert.Equal(5d, r.Days[0].PlannableHours);   // 10h nominal × 50%
    }
}

/// <summary>§1380 — the task calendar's lane layout (pure).</summary>
public sealed class VolunteerCalendarLayoutTests
{
    private static readonly DateOnly Day = new(2027, 2, 10);
    private static TimeWindow W(int h1, int m1, int h2, int m2) => new(new TimeOnly(h1, m1), new TimeOnly(h2, m2));

    [Fact]
    public void Overlapping_tasks_in_one_bucket_sit_side_by_side()
    {
        var days = VolunteerCalendarLayout.Build(new[]
        {
            new CalendarTaskInput(1, 10, Day, W(8, 0, 10, 0)),
            new CalendarTaskInput(2, 10, Day, W(9, 0, 11, 0)),
            new CalendarTaskInput(3, 10, Day, W(11, 0, 12, 0)),   // starts when #2 ends: its own cluster
        });

        var col = Assert.Single(Assert.Single(days).Columns);
        var b = col.Blocks.ToDictionary(x => x.TaskId);
        Assert.Equal((0, 2), (b[1].Lane, b[1].Lanes));
        Assert.Equal((1, 2), (b[2].Lane, b[2].Lanes));
        Assert.Equal((0, 1), (b[3].Lane, b[3].Lanes));
    }

    [Fact]
    public void Buckets_are_columns_and_the_day_spans_whole_hours_around_the_tasks()
    {
        var days = VolunteerCalendarLayout.Build(new[]
        {
            new CalendarTaskInput(1, 20, Day, W(6, 40, 9, 0)),
            new CalendarTaskInput(2, 10, Day, W(15, 0, 17, 30)),
            new CalendarTaskInput(3, 10, Day, null),                      // no time → listed apart
            new CalendarTaskInput(4, 10, null, W(9, 0, 10, 0)),           // no day → not on any day
        });

        var d = Assert.Single(days);
        Assert.Equal(new[] { 10, 20 }, d.Columns.Select(c => c.Key));
        Assert.Equal(new TimeOnly(6, 0), d.From);
        Assert.Equal(new TimeOnly(18, 0), d.To);
        Assert.Equal(new[] { 3 }, d.UntimedTaskIds);
    }
}
