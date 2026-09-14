using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1134 — the volunteer availability form's per-day START TIMES.
///
/// <para>Operator 2026-08-25, verbatim: <i>"Setup day kan godt være 09:00 / Check-in pre-day: 07:00 /
/// Check-in mainday day: 06:40"</i>, and <i>"remember it is for specific days"</i>.</para>
///
/// <para>🔴 <b>The dates are the load-bearing part.</b> `VolunteerDayOptions`'s own comments called
/// 09-Feb "main day 1" and 10-Feb "main day 2", while config `crewDays` — the authority — has 09-Feb
/// as the PRE-DAY and 10-Feb as the MAIN day. Following the comments would have put the 06:40
/// main-day check-in on the pre-day and 07:00 on the main day: both times present, both on the wrong
/// morning, and nothing failing. These tests pin the mapping so the next reader cannot repeat it.</para>
/// </summary>
public sealed class VolunteerDayStartTimeTests
{
    private static readonly DateOnly SetupDay = new(2027, 2, 8);
    private static readonly DateOnly PreDay   = new(2027, 2, 9);
    private static readonly DateOnly MainDay  = new(2027, 2, 10);

    private static string SubOf(DateOnly day, string title) =>
        VolunteerDayOptions.For(day).Single(o => o.Title == title).Sub ?? string.Empty;

    // ── The three start times, on the three right days ───────────────────────────────────

    [Theory]
    [InlineData(2027, 2, 8, "09:00")]   // Setup day
    [InlineData(2027, 2, 9, "07:00")]   // Pre-day (master class)
    [InlineData(2027, 2, 10, "06:40")]  // Main conference day
    public void Full_day_states_when_that_day_starts(int y, int m, int d, string expected)
    {
        var sub = SubOf(new DateOnly(y, m, d), "Full day");

        Assert.Contains(expected, sub);
        Assert.Contains("whole day available", sub);
    }

    [Theory]
    [InlineData(2027, 2, 8, "9–12")]
    [InlineData(2027, 2, 9, "7–12")]
    [InlineData(2027, 2, 10, "6:40–12")]
    public void The_morning_window_opens_at_that_days_start(int y, int m, int d, string expected) =>
        Assert.Equal(expected, SubOf(new DateOnly(y, m, d), "Morning"));

    [Fact]
    public void The_pre_day_and_the_main_day_are_NOT_swapped()
    {
        // 🔑 The single assertion this file exists for. 09-Feb is the PRE-day (07:00); 10-Feb is the
        // MAIN day and starts EARLIER (06:40) because check-in opens before the conference does.
        Assert.Contains("07:00", SubOf(PreDay, "Full day"));
        Assert.Contains("06:40", SubOf(MainDay, "Full day"));

        Assert.DoesNotContain("06:40", SubOf(PreDay, "Full day"));
        Assert.DoesNotContain("07:00", SubOf(MainDay, "Full day"));
    }

    [Fact]
    public void The_afternoon_windows_are_unchanged()
    {
        // 🔒 He changed START times only. An afternoon window quietly moving with them would be a
        // change nobody asked for, on a form volunteers commit against.
        Assert.Equal("12–17", SubOf(SetupDay, "Afternoon"));
        Assert.Equal("12–18", SubOf(PreDay, "Afternoon"));
        Assert.Equal("12–17", SubOf(MainDay, "Afternoon"));
    }

    // ── Saved answers must survive the Slot change ───────────────────────────────────────

    [Fact]
    public void A_volunteer_who_saved_the_OLD_morning_slot_still_re_selects_Morning()
    {
        // 🔴 The migration question. `Slot` is the stored identity and it embeds the hours, so
        // "Morning 9–12" no longer exists on the pre-day or the main day. Resolve falls back to the
        // first option whose LEVEL matches, and Morning is the first Half option on both days —
        // so the saved row still lands on Morning and nothing needs migrating.
        foreach (var day in new[] { PreDay, MainDay })
        {
            var resolved = VolunteerDayOptions.Resolve(
                day, VolunteerAvailabilityLevel.Half, "[Morning 9–12] blocked 13:00 for a session");

            Assert.Equal("Morning", resolved.Title);
        }
    }

    [Fact]
    public void A_saved_AFTERNOON_still_matches_exactly()
    {
        // Afternoon slots did not change, so this one resolves by exact Slot rather than by fallback
        // — which is what keeps Morning and Afternoon (both Half) distinguishable.
        var resolved = VolunteerDayOptions.Resolve(
            PreDay, VolunteerAvailabilityLevel.Half, "[Afternoon 12–18]");

        Assert.Equal("Afternoon", resolved.Title);
    }

    [Fact]
    public void Full_day_keeps_its_stable_slot_across_all_three_days()
    {
        // 🔒 Only the DISPLAY carries the hour; the Slot stays "Full day" so every already-saved
        // full-day row keeps matching exactly, on every day.
        foreach (var day in new[] { SetupDay, PreDay, MainDay })
        {
            Assert.Equal("Full day", VolunteerDayOptions.For(day).Single(o => o.Title == "Full day").Slot);
        }
    }

    // ── §1138 — the main-day "attending, can help afterwards" wording ────────────────────

    [Fact]
    public void The_main_day_attending_box_offers_evening_help()
    {
        // ⚰️ §1138 → §1138a → §1138b, all on 2026-08-26, ending exactly where it started: the long
        // "I can help pack down after event from 17:00" was tried, shortened, then reverted to the
        // original "can help in the evening".
        //
        // 🔑 The test is KEPT rather than deleted along with the wording. It was never really about
        // the sentence: it pins that the main-day box says something DIFFERENT from the pre-day one,
        // which is the ONLY thing separating two cards that share a heading.
        var o = VolunteerDayOptions.For(MainDay).Single(x => x.Title.StartsWith("Attending"));

        Assert.Equal("Attending conference", o.Title);
        Assert.Equal("can help in the evening", o.Sub);
        Assert.NotEqual(SubOf(PreDay, "Attending conference"), o.Sub);
    }

    [Fact]
    public void Renaming_it_did_NOT_change_the_stored_identity()
    {
        // 🔴 Slot is what is persisted in the row's Note and matched on load. Had the rename touched
        // it, every volunteer who already chose this option would stop matching it exactly and fall
        // back by Level — landing on Morning, the first Half option, which is a different answer
        // from the one they gave.
        var o = VolunteerDayOptions.For(MainDay).Single(x => x.Title.StartsWith("Attending"));
        Assert.Equal("Attending conference — can help evening", o.Slot);

        var resolved = VolunteerDayOptions.Resolve(
            MainDay, VolunteerAvailabilityLevel.Half, "[Attending conference — can help evening]");
        Assert.Equal("Attending conference", resolved.Title);
    }

    [Fact]
    public void The_PRE_day_attending_box_is_untouched()
    {
        // 🔒 Both boxes render under the same heading, so it is worth pinning that only the main-day
        // one moved. The pre-day option is "attending only" — it cannot help pack down, and offering
        // it would be wrong on a form people commit against.
        var o = VolunteerDayOptions.For(PreDay).Single(x => x.Title.StartsWith("Attending"));

        Assert.Equal("Attending conference", o.Title);
        Assert.Equal("attending only — not working", o.Sub);
        Assert.Equal(VolunteerAvailabilityLevel.Blocked, o.Level);
    }

    [Fact]
    public void It_still_counts_as_HALF_availability()
    {
        // ⚠️ They ARE giving time, so the level must stay Half. Making it Blocked would change what
        // AvailabilityAutoAssignEngine scores them at — a scheduling change nobody asked for.
        var o = VolunteerDayOptions.For(MainDay).Single(x => x.Title.StartsWith("Attending"));

        Assert.Equal(VolunteerAvailabilityLevel.Half, o.Level);
    }

    // ── The generic fallback must not invent a time ──────────────────────────────────────

    [Fact]
    public void A_day_with_no_configured_start_time_shows_none()
    {
        // ⚠️ Other editions and test fixtures hit the generic set. Printing an invented check-in
        // time on a form a volunteer commits against is worse than printing none.
        var sub = SubOf(new DateOnly(2030, 6, 1), "Full day");

        Assert.Equal("whole day available", sub);
        Assert.DoesNotContain("from", sub);
    }
}
