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
    public sealed record Option(
        string Slot,
        string Title,
        string? Sub,
        VolunteerAvailabilityLevel Level,
        bool IsExclusive);

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
    private static Option Full(string? startsAt = null) =>
        new("Full day", "Full day",
            startsAt is null ? "whole day available" : $"whole day available — from {startsAt}",
            VolunteerAvailabilityLevel.Full, true);

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
    private static Option Morning(string hours) =>
        new($"Morning {hours}", "Morning", hours, VolunteerAvailabilityLevel.Half, false);

    private static Option Afternoon(string hours) =>
        new($"Afternoon {hours}", "Afternoon", hours, VolunteerAvailabilityLevel.Half, false);

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
    private static Option AttendingCanHelpEvening() =>
        new("Attending conference — can help evening", "Attending conference",
            "can help in the evening", VolunteerAvailabilityLevel.Half, false);

    /// <summary>
    /// The ordered options for a given day. ELDK27 days have curated sets; any
    /// other day (other editions / test fixtures) falls back to the classic
    /// Full / Half / Attending-only / Not-able set so the pages never break.
    /// </summary>
    public static IReadOnlyList<Option> For(DateOnly day)
    {
        if (day == PackingDay)
            return new[]
            {
                new Option("Yes, I can help", "Yes, I can help", null, VolunteerAvailabilityLevel.Full, true),
                new Option("No, I cannot help", "No, I cannot help", null, VolunteerAvailabilityLevel.Unavailable, true),
            };

        // §1134 — per-day START TIMES (operator 2026-08-25, in his words): "Setup day kan godt være
        // 09:00 / Check-in pre-day: 07:00 / Check-in mainday day: 06:40".
        if (day == MonSetup)
            return new[] { Full("09:00"), Morning("9–12"), Afternoon("12–17"), NotAble() };

        if (day == PreDay)
            return new[] { Full("07:00"), Morning("7–12"), Afternoon("12–18"), Attending(), NotAble() };

        if (day == MainDay)
            return new[]
            {
                Full("06:40"), Morning("6:40–12"), Afternoon("12–17"),
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
}
