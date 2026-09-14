using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// §1146 — the at-a-glance availability grid: one row per volunteer, two cells per event day.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-28: <i>"i need to have an overview view, where i can see the information
/// from volunteer sign-ups in list form, primarely to preselect people based on their availability
/// … with colos like GREEN, RED, so we have a quick overview per day (morning,evening). If a person
/// selected full day, then it is green in both fields. and show the amount below so we have a
/// number"</i>.</para>
///
/// <para>🔑 <b>The grid is DERIVED, never a second source.</b> The stored shape is one
/// <see cref="VolunteerAvailabilityLevel"/> + a slot tag per (event, participant, day) —
/// <see cref="VolunteerDayOptions.Resolve"/> already turns that pair back into the option the
/// volunteer actually picked, and this maps that option onto two half-day cells. Re-deriving the
/// meaning from <c>Level</c> alone would be a second definition of "what did they choose", and §759
/// is explicit that a mirror is not a shared definition.</para>
///
/// <para>⚠️ <b>He said "morning, evening"; the form offers Morning and AFTERNOON.</b> The columns are
/// named for what volunteers were actually asked, because a column labelled "evening" over
/// afternoon data would be a quiet lie on the page he preselects from. The one genuine evening
/// commitment — the main day's <i>"attending, I can help pack down after event from 17:00"</i>
/// (§1138) — gets its own state rather than being flattened into either colour.</para>
/// </remarks>
public static class VolunteerAvailabilityGrid
{
    /// <summary>The colour of one half-day cell.</summary>
    public enum Cell
    {
        /// <summary>No answer stored for this day at all — not the same as "cannot help".</summary>
        Unknown = 0,

        /// <summary>🟢 Available to work this half of the day.</summary>
        Available = 1,

        /// <summary>🔴 Not available to work this half.</summary>
        Unavailable = 2,

        /// <summary>
        /// 🟡 Attending the conference, but has offered to help AFTER it (main day, from 17:00).
        /// </summary>
        /// <remarks>
        /// 🔒 Deliberately NOT green: they are not available during the afternoon shift, and showing
        /// them as available would put someone on a 12–17 slot they explicitly did not accept.
        /// Deliberately not red either — it is a real offer of help, and losing it in a sea of red
        /// is exactly the information he is building this page to see.
        /// </remarks>
        EveningOnly = 3,
    }

    /// <summary>One volunteer's two cells for one day.</summary>
    public readonly record struct DayCells(Cell Morning, Cell Afternoon)
    {
        /// <summary>Counts toward the day's morning total.</summary>
        public bool MorningCounts => Morning == Cell.Available;

        /// <summary>Counts toward the day's afternoon total.</summary>
        public bool AfternoonCounts => Afternoon == Cell.Available;
    }

    /// <summary>
    /// Map a stored (level, note) answer for <paramref name="day"/> onto the two half-day cells.
    /// </summary>
    /// <param name="hasAnswer">
    /// False when the volunteer has no row for this day at all. 🔒 Distinct from "cannot help":
    /// an unanswered day is a question to chase, a refused day is an answer to respect, and painting
    /// the first red would tell him he has been turned down by people who were never asked.
    /// </param>
    public static DayCells CellsFor(
        DateOnly day, VolunteerAvailabilityLevel level, string? note, bool hasAnswer = true)
    {
        if (!hasAnswer) return new DayCells(Cell.Unknown, Cell.Unknown);

        var option = VolunteerDayOptions.Resolve(day, level, note);

        // 🔒 HIS RULE, STATED FIRST: "If a person selected full day, then it is green in both
        // fields." Keyed on the option's stable Slot, not on Level — the packing day's
        // "Yes, I can help" is also Level.Full and equally means both halves.
        if (option.IsExclusive && option.Level == VolunteerAvailabilityLevel.Full)
            return new DayCells(Cell.Available, Cell.Available);

        // The main-day pack-down offer (§1138): attending, but helping from 17:00.
        if (IsEveningHelp(option))
            return new DayCells(Cell.Unavailable, Cell.EveningOnly);

        // Attending-only / not able / absent ⇒ no working capacity either half.
        if (option.Level is VolunteerAvailabilityLevel.Blocked or VolunteerAvailabilityLevel.Unavailable)
            return new DayCells(Cell.Unavailable, Cell.Unavailable);

        // The two half-day options are told apart by their Slot, which is exactly why the slot is
        // persisted: Morning and Afternoon share Level.Half and are otherwise indistinguishable.
        if (StartsWith(option.Slot, "Morning"))
            return new DayCells(Cell.Available, Cell.Unavailable);

        if (StartsWith(option.Slot, "Afternoon"))
            return new DayCells(Cell.Unavailable, Cell.Available);

        // The generic fallback set's plain "Half day" carries no half, so neither cell may claim
        // one. Reported as unknown rather than guessed — a wrong half is a missed shift.
        if (option.Level == VolunteerAvailabilityLevel.Half)
            return new DayCells(Cell.Unknown, Cell.Unknown);

        return new DayCells(Cell.Available, Cell.Available);
    }

    /// <summary>The §1138 main-day option: attending the conference, but helping in the evening.</summary>
    /// <remarks>
    /// ⚠️ Matched on the SLOT, and the slot only. This option's <see cref="VolunteerAvailabilityLevel"/>
    /// is <c>Half</c> — deliberately, per §1138: <i>"they ARE giving time"</i>, and changing it would
    /// move what the auto-assign engine scores them at. So level cannot identify it, and neither can
    /// the display text, which was reworded three times in one afternoon (§1138 → a → b) while the
    /// slot was never touched. The slot is the stored identity; it is the only stable key here.
    /// </remarks>
    private static bool IsEveningHelp(VolunteerDayOptions.Option option) =>
        option.Slot.Contains("can help evening", StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The colour token for a cell — one definition, so both pages paint the same.</summary>
    public static string CssClass(Cell cell) => cell switch
    {
        Cell.Available => "vg-yes",
        Cell.Unavailable => "vg-no",
        Cell.EveningOnly => "vg-eve",
        _ => "vg-unknown",
    };

    /// <summary>The cell's short label. Kept out of the colour so the grid is readable without colour.</summary>
    public static string Label(Cell cell) => cell switch
    {
        Cell.Available => "Yes",
        Cell.Unavailable => "No",
        Cell.EveningOnly => "Eve",
        _ => "–",
    };
}
