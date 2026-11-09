namespace CommunityHub.Core.Volunteers;

/// <summary>A task placed in the calendar: which lane of its bucket column it sits in, and how many lanes that
/// column needs at that time (so overlapping tasks sit side by side instead of on top of each other).</summary>
public sealed record CalendarBlock(int TaskId, int ColumnKey, TimeWindow Window, int Lane, int Lanes);

/// <summary>One bucket column on one day.</summary>
public sealed record CalendarColumn(int Key, IReadOnlyList<CalendarBlock> Blocks);

/// <summary>One day: the time range to draw and the columns.</summary>
public sealed record CalendarDay(DateOnly Day, TimeOnly From, TimeOnly To, IReadOnlyList<CalendarColumn> Columns, IReadOnlyList<int> UntimedTaskIds);

/// <summary>Input row for <see cref="VolunteerCalendarLayout"/>.</summary>
public sealed record CalendarTaskInput(int TaskId, int ColumnKey, DateOnly? Day, TimeWindow? Window);

/// <summary>
/// §1380 — lays volunteer tasks out as a schedule-builder grid (Leth 2026-11-01: <i>"på samme måde som sessionize
/// viser schedule builderen"</i>): one column per bucket, time running down. PURE and deterministic.
/// </summary>
public static class VolunteerCalendarLayout
{
    public static IReadOnlyList<CalendarDay> Build(IEnumerable<CalendarTaskInput> tasks)
    {
        var days = new List<CalendarDay>();
        foreach (var g in tasks.Where(t => t.Day is not null).GroupBy(t => t.Day!.Value).OrderBy(g => g.Key))
        {
            var timed = g.Where(t => t.Window is not null).ToList();
            var untimed = g.Where(t => t.Window is null).Select(t => t.TaskId).OrderBy(i => i).ToList();

            var from = timed.Count == 0 ? new TimeOnly(8, 0) : new TimeOnly(timed.Min(t => t.Window!.Value.Start.Hour), 0);
            var lastEnd = timed.Count == 0 ? new TimeOnly(17, 0) : timed.Max(t => t.Window!.Value.End);
            var to = lastEnd.Minute == 0 ? lastEnd : (lastEnd.Hour >= 23 ? new TimeOnly(23, 59) : new TimeOnly(lastEnd.Hour + 1, 0));

            var columns = timed
                .GroupBy(t => t.ColumnKey)
                .OrderBy(c => c.Key)
                .Select(c => new CalendarColumn(c.Key, Lanes(c.ToList())))
                .ToList();

            days.Add(new CalendarDay(g.Key, from, to, columns, untimed));
        }
        return days;
    }

    /// <summary>
    /// Greedy interval colouring: earliest start first, each task takes the first lane that is free at its
    /// start. Then every task in an overlapping cluster gets the cluster's lane count, so widths line up.
    /// </summary>
    private static IReadOnlyList<CalendarBlock> Lanes(List<CalendarTaskInput> items)
    {
        var sorted = items.OrderBy(t => t.Window!.Value.Start).ThenByDescending(t => t.Window!.Value.End).ThenBy(t => t.TaskId).ToList();
        var result = new List<CalendarBlock>();
        var cluster = new List<(CalendarTaskInput T, int Lane)>();
        var laneEnds = new List<TimeOnly>();
        TimeOnly clusterEnd = default;

        void Flush()
        {
            var lanes = cluster.Count == 0 ? 0 : cluster.Max(x => x.Lane) + 1;
            result.AddRange(cluster.Select(x => new CalendarBlock(x.T.TaskId, x.T.ColumnKey, x.T.Window!.Value, x.Lane, lanes)));
            cluster.Clear();
            laneEnds.Clear();
        }

        foreach (var t in sorted)
        {
            var w = t.Window!.Value;
            if (cluster.Count > 0 && w.Start >= clusterEnd) Flush();
            var lane = laneEnds.FindIndex(end => end <= w.Start);
            if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(w.End); } else laneEnds[lane] = w.End;
            cluster.Add((t, lane));
            clusterEnd = cluster.Count == 1 ? w.End : (w.End > clusterEnd ? w.End : clusterEnd);
        }
        Flush();
        return result;
    }
}
