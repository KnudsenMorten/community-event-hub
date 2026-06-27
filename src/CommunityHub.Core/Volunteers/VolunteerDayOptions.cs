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
    private static readonly DateOnly PackingDay = new(2027, 2, 7);  // Sun — packing 9–14
    private static readonly DateOnly MonSetup   = new(2027, 2, 8);  // Mon — setup / pre-day
    private static readonly DateOnly TueDay1    = new(2027, 2, 9);  // Tue — main day 1 (conference)
    private static readonly DateOnly WedDay2    = new(2027, 2, 10); // Wed — main day 2 (conference)

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
    private static Option Full() =>
        new("Full day", "Full day", "whole day available", VolunteerAvailabilityLevel.Full, true);

    private static Option Morning() =>
        new("Morning 9–12", "Morning", "9–12", VolunteerAvailabilityLevel.Half, false);

    private static Option Afternoon(string hours) =>
        new($"Afternoon {hours}", "Afternoon", hours, VolunteerAvailabilityLevel.Half, false);

    private static Option NotAble() =>
        new("Not able to help", "Not able to help", "I cannot help this day", VolunteerAvailabilityLevel.Unavailable, true);

    private static Option Attending() =>
        new("Attending conference", "Attending conference", "attending only — not working", VolunteerAvailabilityLevel.Blocked, false);

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

        if (day == MonSetup)
            return new[] { Full(), Morning(), Afternoon("12–17"), NotAble() };

        if (day == TueDay1)
            return new[] { Full(), Morning(), Afternoon("12–18"), Attending(), NotAble() };

        if (day == WedDay2)
            return new[] { Full(), Morning(), Afternoon("12–17"), AttendingCanHelpEvening(), NotAble() };

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
