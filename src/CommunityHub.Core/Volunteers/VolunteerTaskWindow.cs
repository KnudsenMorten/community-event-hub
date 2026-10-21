using System.Globalization;
using System.Text.RegularExpressions;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// §1297 — the TASK side of the planner's time model: turning a
/// <see cref="VolunteerTask"/> into the clock window it has to be staffed for.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Why both a typed column and a parser.</b> <see cref="VolunteerTask.Shift"/> and
/// <see cref="VolunteerTask.TimeEnd"/> are free text by design (the shift catalogue is
/// config-driven, and the Excel round-trip and the plan CSV both write whatever the organizer
/// typed). All 127 PROD tasks happen to hold clean <c>HH:mm</c>, so the typed
/// <see cref="VolunteerTask.StartTime"/>/<see cref="VolunteerTask.EndTime"/> columns are
/// backfilled and kept in step on every write. <b>But the parser stays the fallback</b>, so a
/// task that arrives from an import path we have not touched, or from another edition, still
/// schedules instead of silently dropping out of the plan.</para>
///
/// <para>⚠️ <b>A task with no usable window is UNSCHEDULED, not unstaffable.</b> The planner
/// still fills it — it just cannot reason about overlaps for it. Treating "no time recorded"
/// as "needs nobody" would quietly shrink the plan.</para>
/// </remarks>
public static class VolunteerTaskWindow
{
    // First H:mm / HH:mm (or HH.mm) anywhere in the string. Deliberately lenient about what
    // surrounds it: the column has held "08:00", "Day 1, 08:00–10:00" and "kl. 8.00" in
    // different editions, and all three mean the same hour.
    //
    // ⚠️ The boundary guards are load-bearing. Without them "25:00" matches its own tail as
    // "5:00" — a nonsense value silently becoming a plausible one, which is worse than refusing
    // it, because the task would then be scheduled against an hour nobody wrote.
    private static readonly Regex ClockTime = new(
        @"(?<![\d:.])(?<h>[01]?\d|2[0-3])\s*[:.]\s*(?<m>[0-5]\d)(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The first clock time in a free-text shift value, or null when there is none.
    /// </summary>
    public static TimeOnly? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = ClockTime.Match(text);
        if (!m.Success) return null;

        var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        return new TimeOnly(h, min);
    }

    /// <summary>
    /// The task's work window: the typed columns when present, else parsed from the free-text
    /// <see cref="VolunteerTask.Shift"/> / <see cref="VolunteerTask.TimeEnd"/>. Null when the
    /// task has no usable pair (missing end, unparseable text, or an end at/before the start).
    /// </summary>
    public static TimeWindow? WindowOf(VolunteerTask task) =>
        TimeWindow.TryCreate(
            task.StartTime ?? ParseTime(task.Shift),
            task.EndTime ?? ParseTime(task.TimeEnd));

    /// <summary>
    /// Re-derive <see cref="VolunteerTask.StartTime"/>/<see cref="VolunteerTask.EndTime"/> from
    /// the free-text values. Called from every path that writes Shift/TimeEnd so the typed
    /// columns can never drift from the text an organizer actually sees.
    /// </summary>
    /// <remarks>
    /// 🔒 The text stays the source of truth and the typed columns are its projection — the
    /// additive-only rule. Duration is <c>End − Start</c>, computed; it is deliberately NOT a
    /// stored field, because storing a duration beside a start and an end gives three values
    /// that can disagree.
    /// </remarks>
    public static void SyncTypedTimes(VolunteerTask task)
    {
        task.StartTime = ParseTime(task.Shift);
        task.EndTime = ParseTime(task.TimeEnd);
    }
}
