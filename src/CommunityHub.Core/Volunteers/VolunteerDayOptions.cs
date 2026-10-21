using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// The per-day volunteer AVAILABILITY option sets, shared by the public sign-up
/// survey (<c>/volunteer/signup</c>) and the self-service portal form
/// (<c>/volunteer/availability</c>) so both pages always offer EXACTLY the same
/// days, slots, labels and exclusivity rules (REQUIREMENTS §45).
///
/// The ELDK27 days each have a distinct, hand-curated slot set (operator
/// 2026-06-25). Every option maps onto the EXISTING storage shape — a single
/// <see cref="VolunteerAvailabilityLevel"/> per (event, participant, day) — so NO
/// schema change is needed. Options that share a Level (e.g. Morning vs Afternoon
/// both = Half) are disambiguated by the option's stable <see cref="Slot"/> text,
/// which is persisted in <see cref="VolunteerDayAvailability.Note"/> (the only
/// free string on the row) and re-read on load to re-select the right radio.
/// </summary>
public static class VolunteerDayOptions
{
    // ELDK27 dates (kept in sync with config/event.eldk27.json "dates").
    // 🔒 §1134 — the day NAMES here were wrong and are corrected against config `crewDays`, which is
    // the authority: 02-08 setup, 02-09 PRE-DAY (master class), 02-10 MAIN conference day. The old
    // comments read "setup / pre-day", "main day 1" and "main day 2", which would have put the
    // main-day check-in on the pre-day — the exact mistake this edit had to avoid.
    private static readonly DateOnly PackingDay = new(2027, 2, 7);  // Sun — packing 9–14
    private static readonly DateOnly MonSetup   = new(2027, 2, 8);  // Mon — SETUP day,   starts 09:00
    private static readonly DateOnly PreDay     = new(2027, 2, 9);  // Tue — PRE-DAY,     check-in 07:00
    private static readonly DateOnly MainDay    = new(2027, 2, 10); // Wed — MAIN day,    check-in 06:40

    /// <summary>
    /// One selectable availability option for a day. <see cref="Slot"/> is the
    /// stable identity that is stored (in Note) and matched on load — keep it
    /// constant even if the display <see cref="Title"/>/<see cref="Sub"/> changes.
    /// <see cref="IsExclusive"/> marks the "all-or-nothing" choices (Full day /
    /// Not able to help) whose selection clears every other choice client-side.
    /// </summary>
    /// <param name="Window">
    /// §1297 — the option's hours as TYPED clock times, for the planner. <c>null</c> means
    /// "no stated hours" (packing day, and the generic fallback set), which the matcher
    /// reads as "available for anything that day" — see <see cref="DayWindow"/>.
    /// <para>🔒 <b>This exists so nothing ever parses <see cref="Sub"/> or <see cref="Slot"/>
    /// to recover the hours.</b> The Slot is a STORAGE identity and stale ones survive in
    /// the database on purpose (a row saved as <c>[Morning 9–12]</c> still resolves to the
    /// pre-day's <c>Morning 7–12</c> by Level fallback). Reading hours out of the stored
    /// string would therefore schedule someone against a window that no longer exists.
    /// Always go through <see cref="Resolve"/> to get the CURRENT option, then read this.</para>
    /// </param>
    public sealed record Option(
        string Slot,
        string Title,
        string? Sub,
        VolunteerAvailabilityLevel Level,
        bool IsExclusive,
        TimeWindow? Window = null);

    // --- Reusable option fragments -----------------------------------------
    /// <summary>
    /// §1134 — <paramref name="startsAt"/> is the day's START TIME, shown so a volunteer choosing
    /// "Full day" knows what they are committing to. 🔒 The <c>Slot</c> stays the constant
    /// <c>"Full day"</c>: it is the stored identity, so only the DISPLAY carries the hour.
    /// </summary>
    /// <remarks>
    /// 🔒 <paramref name="startsAt"/> is OPTIONAL and omitted by the generic fallback set, which
    /// serves other editions and test fixtures. Those days have no known check-in time, and printing
    /// an invented one on a form a volunteer commits against is worse than printing none.
    /// </remarks>
    /// <param name="sub">
    /// §1297a — an explicit display line, for a day whose full-day window does NOT simply run to
    /// the afternoon option's end. 🔒 Used so a widened window is VISIBLE on the form rather than
    /// only true inside the planner: a volunteer reading "from 07:00" next to "Afternoon 12–18"
    /// reasonably concludes the day ends at 18:00, and quietly scheduling them to 18:30 would be
    /// holding them to something the page never said.
    /// </param>
    private static Option Full(string? startsAt = null, TimeWindow? window = null, string? sub = null) =>
        new("Full day", "Full day",
            sub ?? (startsAt is null ? "whole day available" : $"whole day available — from {startsAt}"),
            VolunteerAvailabilityLevel.Full, true, window);

    /// <summary>
    /// §1134 — the morning window now differs PER DAY (operator 2026-08-25: setup 09:00, pre-day
    /// check-in 07:00, main-day check-in 06:40), so the hours are a parameter rather than a
    /// hard-coded 9–12.
    /// </summary>
    /// <remarks>
    /// ⚠️ The <c>Slot</c> embeds the hours, so it CHANGES for the pre-day and the main day — and the
    /// Slot is the stored identity. That is safe here, and deliberately so: <see cref="Resolve"/>
    /// falls back to <i>the first option whose Level matches</i> when a stored slot no longer
    /// exists, and Morning is the first <c>Half</c> option on every one of these days. So a
    /// volunteer who saved <c>[Morning 9–12]</c> still re-selects Morning, and their next save
    /// re-tags the note with the new window. Nothing is lost and nothing has to be migrated.
    /// </remarks>
    private static Option Morning(string hours, TimeWindow? window = null) =>
        new($"Morning {hours}", "Morning", hours, VolunteerAvailabilityLevel.Half, false, window);

    private static Option Afternoon(string hours, TimeWindow? window = null) =>
        new($"Afternoon {hours}", "Afternoon", hours, VolunteerAvailabilityLevel.Half, false, window);

    /// <summary>A clock time written the way the day sets below read best: <c>At(6, 40)</c>.</summary>
    private static TimeOnly At(int hour, int minute = 0) => new(hour, minute);

    private static TimeWindow Window(TimeOnly from, TimeOnly to) => new(from, to);

    private static Option NotAble() =>
        new("Not able to help", "Not able to help", "I cannot help this day", VolunteerAvailabilityLevel.Unavailable, true);

    private static Option Attending() =>
        new("Attending conference", "Attending conference", "attending only — not working", VolunteerAvailabilityLevel.Blocked, false);

    /// <summary>
    /// §1138 — the MAIN-DAY "attending, but I can help afterwards" option.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-26: <i>"Rename Attending conference-box to 'Attending conference full
    /// day - I can help pack down after event from 17:00'"</i>.</para>
    ///
    /// <para>🔑 <b>This one, not <see cref="Attending"/>.</b> Both render the title "Attending
    /// conference", so the page shows two boxes with the same heading. His wording decides it: the
    /// MAIN day's afternoon window ends at 17:00, so "pack down after event from 17:00" is this box.
    /// The pre-day one is "attending only — not working" and cannot help with anything.</para>
    ///
    /// <para>🔒 <b>The Slot is deliberately UNCHANGED</b> — it is the stored identity (see the class
    /// remarks: <i>"keep it constant even if the display Title/Sub changes"</i>). Every volunteer who
    /// already picked this option keeps matching it exactly; only what they read changes.</para>
    ///
    /// <para>⚠️ Still <c>Half</c>, not <c>Blocked</c>: they ARE giving time. Changing the level would
    /// alter what <c>AvailabilityAutoAssignEngine</c> scores them at, which is a scheduling change
    /// he did not ask for.</para>
    /// </remarks>
    /// <remarks>
    /// <para>⚰️ §1138 → §1138a → §1138b, all on 2026-08-26, ending exactly where it started:
    /// <i>"Rename Attending conference-box to 'Attending conference full day - I can help pack down
    /// after event from 17:00'"</i> → <i>"remove the 'full day'"</i> → <i>"change the wording 'I can
    /// help pack down after event from 17:00' to 'can help in the evening'"</i>.</para>
    ///
    /// <para>🔑 The wording is back to the original. The longer sentence read as a COMMITMENT to a
    /// specific job at a specific hour; "can help in the evening" states availability and leaves what
    /// the help is to the schedule — which is what an availability form is for.</para>
    ///
    /// <para>🔒 <c>Slot</c> was never touched across any of the three, so no volunteer's saved answer
    /// moved at any point. That is the whole reason the display could be changed three times in an
    /// afternoon on a live form without consequence.</para>
    /// </remarks>
    /// <remarks>
    /// <para>🔴 §1297 — <b>this option is <c>Half</c> but it means EVENING, and that is exactly
    /// why it carries an explicit window.</b> A planner reading "Half ⇒ morning or afternoon"
    /// would schedule these people into the main-day morning, which is precisely when they are
    /// attending the conference. The 17:00–21:00 window is the main day's pack-down block, which
    /// is what "can help in the evening" was written for.</para>
    /// </remarks>
    private static Option AttendingCanHelpEvening() =>
        new("Attending conference — can help evening", "Attending conference",
            "can help in the evening", VolunteerAvailabilityLevel.Half, false,
            Window(At(17), At(21)));

    /// <summary>
    /// The ordered options for a given day. ELDK27 days have curated sets; any
    /// other day (other editions / test fixtures) falls back to the classic
    /// Full / Half / Attending-only / Not-able set so the pages never break.
    /// </summary>
    public static IReadOnlyList<Option> For(DateOnly day)
    {
        // 🔒 §1297 — the packing day is BINARY and carries NO window, deliberately. Operator
        // 2026-09-22: *"packing day is a few tasks where people will be there all time · there
        // are no 50/50 split that day · think of it as full day, either they come or not."*
        // ⇒ "Yes" means present for whatever runs, so the planner does no hour-matching here.
        // Giving it a 09:00–14:00 window would make the day's own task (*Pack attendee bags*,
        // 09:00–15:00) unfillable by the 17 people who said yes to exactly it.
        if (day == PackingDay)
            return new[]
            {
                new Option("Yes, I can help", "Yes, I can help", null, VolunteerAvailabilityLevel.Full, true),
                new Option("No, I cannot help", "No, I cannot help", null, VolunteerAvailabilityLevel.Unavailable, true),
            };

        // §1134 — per-day START TIMES (operator 2026-08-25, in his words): "Setup day kan godt være
        // 09:00 / Check-in pre-day: 07:00 / Check-in mainday day: 06:40".
        //
        // 🔒 §1297 — SETUP DAY STARTS 09:00 AND STAYS THERE. It was briefly widened to 08:00 on
        // 2026-09-22 because 18 of Monday's 31 task slots start at 08:00 (`Mount Expo Walls` 14
        // people, `Mount Light Rollups` 4) and a 09:00 form can never staff them. The operator
        // reverted it the same day: *"monday - move it to 9am (move back), so it matches. we dont
        // start 8am on monday."*
        //
        // ⚠️ So the 08:00 starts are a TASK-DATA question, not a form question — the volunteers do
        // not arrive at 08:00, whatever those rows say. Do not "fix" this by widening the form
        // again; the planner reporting those slots as unstaffable is the correct and useful answer.
        //
        // §1297 — each option now also carries its hours as a TYPED window for the planner.
        // 🔑 The windows are DERIVED FROM THE DISPLAY, not invented: "Full day" spans that
        // day's morning start to its afternoon end, which is what a volunteer reading
        // "whole day available — from 09:00" next to "Afternoon 12–17" understands it to mean.
        if (day == MonSetup)
            return new[]
            {
                Full("09:00", Window(At(9), At(17))),
                Morning("9–12", Window(At(9), At(12))),
                Afternoon("12–17", Window(At(12), At(17))),
                NotAble(),
            };

        // §1297a — the PRE-DAY full day runs to 18:30, not 18:00 (operator 2026-09-22, choosing
        // *"widen the pre-day instead"* over moving the two tasks that overrun it: *Appreciation
        // Bags* 17:00–18:30 and *Prepare cloakroom number-tags (900)* 17:50–18:30).
        //
        // 🔒 ONLY "Full day" widened. Afternoon deliberately stays 12–18:
        //   1. It is not needed — 17 people answered Full day on the pre-day against 4 slots.
        //   2. ⚠️ Its Slot embeds the hours ("Afternoon 12–18") and the Slot is the STORED
        //      identity. Changing it would strand the 2 saved afternoon rows, and the Level
        //      fallback resolves an unknown Half slot to the FIRST Half option — Morning. Two
        //      people who said afternoon would silently become morning. Widening what is not
        //      needed would cost exactly the answers we are trying to honour.
        if (day == PreDay)
            return new[]
            {
                Full("07:00", Window(At(7), At(18, 30)), "whole day available — 07:00–18:30"),
                Morning("7–12", Window(At(7), At(12))),
                Afternoon("12–18", Window(At(12), At(18))),
                Attending(), NotAble(),
            };

        if (day == MainDay)
            return new[]
            {
                Full("06:40", Window(At(6, 40), At(17))),
                Morning("6:40–12", Window(At(6, 40), At(12))),
                Afternoon("12–17", Window(At(12), At(17))),
                AttendingCanHelpEvening(), NotAble(),
            };

        // Generic fallback (non-ELDK27 days / other editions / tests).
        return new[]
        {
            Full(),
            new Option("Half day", "Half day", "~50% — split work & attend", VolunteerAvailabilityLevel.Half, false),
            Attending(),
            NotAble(),
        };
    }

    /// <summary>
    /// Pick the option that a stored (Level, Note) pair represents, so a saved
    /// row re-selects the correct radio on load. Prefers an exact Slot match in
    /// the Note (handles Morning vs Afternoon which share a Level), else the
    /// first option whose Level matches, else the first option.
    /// </summary>
    public static Option Resolve(DateOnly day, VolunteerAvailabilityLevel level, string? note)
    {
        var options = For(day);
        var slot = SlotOf(note);
        if (slot is not null)
        {
            var bySlot = options.FirstOrDefault(o =>
                string.Equals(o.Slot, slot, StringComparison.OrdinalIgnoreCase));
            if (bySlot is not null) return bySlot;
        }
        return options.FirstOrDefault(o => o.Level == level) ?? options[0];
    }

    // --- Note encoding: "[slot]" optionally followed by the user's free note ---
    // We persist the chosen slot as a "[slot]" prefix in Note so options that
    // share a Level stay distinguishable, while keeping the volunteer's own
    // free-text note readable after it. Encoding is idempotent: re-saving never
    // stacks prefixes because we always strip any existing one first.

    /// <summary>Combine the chosen slot + the volunteer's free note into the stored Note value.</summary>
    public static string? ComposeNote(string slot, string? userNote)
    {
        var clean = StripSlot(userNote);
        var tag = $"[{slot}]";
        if (string.IsNullOrWhiteSpace(clean)) return tag;
        return $"{tag} {clean}";
    }

    /// <summary>The slot id stored in a Note, or null if none is tagged.</summary>
    public static string? SlotOf(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var s = note.TrimStart();
        if (s.Length > 1 && s[0] == '[')
        {
            var end = s.IndexOf(']');
            if (end > 1) return s.Substring(1, end - 1).Trim();
        }
        return null;
    }

    /// <summary>The volunteer's free-text note with any "[slot]" prefix removed.</summary>
    public static string? StripSlot(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return note;
        var s = note.TrimStart();
        if (s.Length > 1 && s[0] == '[')
        {
            var end = s.IndexOf(']');
            if (end > 1)
            {
                var rest = s.Substring(end + 1).Trim();
                return string.IsNullOrWhiteSpace(rest) ? null : rest;
            }
        }
        return note.Trim();
    }

    /// <summary>Human-readable availability label for lead emails (slot if tagged, else the Level name).</summary>
    public static string DisplayLabel(DateOnly day, VolunteerAvailabilityLevel level, string? note)
        => Resolve(day, level, note).Slot;

    /// <summary>
    /// §1297 — what a stored <c>(Day, Level, Note)</c> row means in CLOCK terms: the single
    /// entry point the planner uses to turn an availability answer into hours.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>It resolves the option first, then reads the option's typed window</b> —
    /// never the stored string. Two things make that mandatory:</para>
    /// <list type="bullet">
    /// <item><b>Stale slot tags are normal.</b> Rows saved as <c>[Morning 9–12]</c> still exist
    /// against the pre-day and the main day, whose morning windows are now 7–12 and 6:40–12.
    /// <see cref="Resolve"/> maps them onto the CURRENT option by Level fallback; parsing the
    /// stored hours would schedule those two people against a window nobody offers.</item>
    /// <item><b>The Level alone is ambiguous.</b> Morning, Afternoon and "Attending — can help
    /// evening" are all <c>Half</c>. Only the resolved option distinguishes them, and getting
    /// that wrong puts an evening helper on a 06:40 check-in.</item>
    /// </list>
    ///
    /// <para>⚠️ The RESOLVED option's Level decides presence, not the stored one. They agree by
    /// construction (the fallback matches on Level), and where an exact slot match wins, the
    /// option is the authority on what that answer means.</para>
    /// </remarks>
    public static DayWindow WindowFor(DateOnly day, VolunteerAvailabilityLevel level, string? note)
    {
        var option = Resolve(day, level, note);

        // Absent (Unavailable) and present-but-attending (Blocked) are both "cannot be
        // given work on this day" — the planner needs no finer distinction than that.
        if (option.Level is VolunteerAvailabilityLevel.Unavailable or VolunteerAvailabilityLevel.Blocked)
            return DayWindow.Absent;

        return option.Window is { } w ? new DayWindow(true, w) : DayWindow.AllDay;
    }
}
