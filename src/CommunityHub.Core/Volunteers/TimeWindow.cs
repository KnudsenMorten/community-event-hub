namespace CommunityHub.Core.Volunteers;

/// <summary>
/// §1297 — a half-open clock-time interval <c>[Start, End)</c> on a single day: the one
/// shape the volunteer planner reasons about. Used for BOTH sides of the match — what a
/// task needs (<see cref="VolunteerTaskWindow"/>) and when a volunteer said they can work
/// (<see cref="VolunteerDayOptions.WindowFor"/>) — so the containment and overlap rules
/// are written once and cannot disagree between the two.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Half-open is the load-bearing choice.</b> Two back-to-back shifts
/// (09:00–12:00 and 12:00–15:00) must NOT count as an overlap — the same person can work
/// both. With a closed interval they would collide at 12:00 and the planner would refuse
/// every consecutive pair, which is most of the plan.</para>
///
/// <para>⚠️ No date, and no midnight wrap. Every ELDK day starts and ends within the same
/// calendar day (the latest is the 17:30–21:00 pack-down), and a window whose End is not
/// after its Start is rejected at construction rather than silently treated as empty —
/// a zero-length shift is a data error, not a shift nobody has to staff.</para>
/// </remarks>
public readonly record struct TimeWindow
{
    public TimeWindow(TimeOnly start, TimeOnly end)
    {
        if (end <= start)
            throw new ArgumentException($"A time window must end after it starts (got {start:HH\\:mm}–{end:HH\\:mm}).", nameof(end));
        Start = start;
        End = end;
    }

    public TimeOnly Start { get; }
    public TimeOnly End { get; }

    /// <summary>Length in hours — the planner's fairness unit (hours already allocated).</summary>
    public double Hours => (End - Start).TotalHours;

    /// <summary>True when <paramref name="other"/> fits entirely inside this window.
    /// This is the ELIGIBILITY test: a volunteer can do a task only if they are there
    /// for ALL of it, not merely for part of it.</summary>
    public bool Contains(TimeWindow other) => other.Start >= Start && other.End <= End;

    /// <summary>True when the two intervals share any time at all. This is the
    /// DOUBLE-BOOKING test. Half-open, so touching endpoints do not overlap.</summary>
    public bool Overlaps(TimeWindow other) => Start < other.End && other.Start < End;

    public override string ToString() => $"{Start:HH\\:mm}–{End:HH\\:mm}";

    /// <summary>Build a window, or null when either end is missing or the pair is
    /// nonsensical (end at or before start). Null means "no usable window", which every
    /// caller treats as "unknown hours" rather than "no time".</summary>
    public static TimeWindow? TryCreate(TimeOnly? start, TimeOnly? end)
    {
        if (start is null || end is null) return null;
        if (end.Value <= start.Value) return null;
        return new TimeWindow(start.Value, end.Value);
    }
}

/// <summary>
/// §1297 — what one volunteer's stored answer for one day means in clock terms.
/// </summary>
/// <param name="Present">
/// False for <c>Unavailable</c> (absent) and <c>Blocked</c> (present but attending, not
/// working). A candidate who is not present that day can never be proposed for a task on it.
/// </param>
/// <param name="Window">
/// The hours they can work, or <b>null meaning "present, hours not stated"</b> — which the
/// matcher treats as available for anything that day.
/// <para>🔑 Null is a real answer, not a missing one, and it covers two deliberate cases:
/// the <b>packing day</b>, which the operator settled as binary (<i>"think of it as full
/// day, either they come or not"</i>), and the <b>generic option set</b> used by other
/// editions and test fixtures, whose days have no configured hours. Inventing a window for
/// either would make the planner refuse work that those people did in fact volunteer for.</para>
/// </param>
public sealed record DayWindow(bool Present, TimeWindow? Window)
{
    /// <summary>The volunteer is not available at all on this day.</summary>
    public static readonly DayWindow Absent = new(false, null);

    /// <summary>Present with no stated hours — matches any task on the day.</summary>
    public static readonly DayWindow AllDay = new(true, null);

    /// <summary>True when a task needing <paramref name="task"/> fits this answer.</summary>
    public bool CanCover(TimeWindow? task)
    {
        if (!Present) return false;
        if (Window is null) return true;   // present, hours not stated — covers anything.
        if (task is null) return true;     // task has no stated hours — the day is the constraint.
        return Window.Value.Contains(task.Value);
    }
}
