using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §1297 — turning a stored availability row into CLOCK HOURS.
///
/// <para>🔑 This is the half of the planner that reads what volunteers actually said. The
/// pre-§1297 engine never selected <c>Note</c> at all, scored everyone with a single
/// <c>Max(Level)</c> across all days, and therefore treated a setup-morning volunteer as a valid
/// candidate for a main-day afternoon task. These tests pin the mapping that replaced it.</para>
/// </summary>
public sealed class VolunteerAvailabilityWindowTests
{
    private static readonly DateOnly PackingDay = new(2027, 2, 7);
    private static readonly DateOnly SetupDay   = new(2027, 2, 8);
    private static readonly DateOnly PreDay     = new(2027, 2, 9);
    private static readonly DateOnly MainDay    = new(2027, 2, 10);

    private static DayWindow For(DateOnly day, VolunteerAvailabilityLevel level, string? note) =>
        VolunteerDayOptions.WindowFor(day, level, note);

    // ── Full day means THAT day's hours, not "all day" ───────────────────────────────────

    [Theory]
    [InlineData(2027, 2, 8, 9, 0, 17, 0)]     // setup — 09:00, and it stays 09:00 (§1297 revert)
    [InlineData(2027, 2, 9, 7, 0, 18, 30)]    // pre-day — 07:00 check-in, widened to 18:30 (§1297a)
    [InlineData(2027, 2, 10, 6, 40, 17, 0)]   // main day — 06:40 check-in
    public void Full_day_spans_that_days_own_hours(int y, int m, int d, int h1, int m1, int h2, int m2)
    {
        var w = For(new DateOnly(y, m, d), VolunteerAvailabilityLevel.Full, "[Full day]");

        Assert.True(w.Present);
        Assert.Equal(new TimeWindow(new TimeOnly(h1, m1), new TimeOnly(h2, m2)), w.Window);
    }

    [Fact]
    public void The_PRE_DAY_full_day_reaches_18_30_and_SAYS_so()
    {
        // §1297a — operator 2026-09-22 chose *"widen the pre-day instead"* of moving the two tasks
        // that overrun it (*Appreciation Bags* 17:00–18:30, *Prepare cloakroom number-tags (900)*
        // 17:50–18:30).
        //
        // 🔒 The DISPLAY must carry the new end too. A widened window that is true only inside the
        // planner holds people to something the form never told them.
        var w = For(PreDay, VolunteerAvailabilityLevel.Full, "[Full day]");
        Assert.True(w.CanCover(new TimeWindow(new TimeOnly(17, 50), new TimeOnly(18, 30))));

        var sub = VolunteerDayOptions.For(PreDay).Single(o => o.Title == "Full day").Sub;
        Assert.Contains("18:30", sub);
    }

    [Fact]
    public void The_pre_day_AFTERNOON_was_deliberately_NOT_widened()
    {
        // 🔴 The tempting "fix" a later reader will reach for, and the reason it is wrong.
        //
        // Afternoon's Slot EMBEDS its hours ("Afternoon 12–18") and the Slot is the STORED
        // identity. Widening it to 12–18:30 would strand every saved afternoon row, and Resolve's
        // Level fallback lands an unknown Half slot on the FIRST Half option — Morning. The two
        // people who answered "afternoon" on the pre-day would silently become morning people.
        //
        // It is also unnecessary: 17 answered Full day on the pre-day, against 4 late slots.
        var afternoon = VolunteerDayOptions.For(PreDay).Single(o => o.Title == "Afternoon");

        Assert.Equal("Afternoon 12–18", afternoon.Slot);
        Assert.Equal("12–18", afternoon.Sub);
        Assert.Equal(new TimeOnly(18, 0), afternoon.Window!.Value.End);

        // The saved row still resolves to Afternoon, by EXACT slot match — not by fallback.
        var resolved = VolunteerDayOptions.Resolve(PreDay, VolunteerAvailabilityLevel.Half, "[Afternoon 12–18]");
        Assert.Equal("Afternoon", resolved.Title);
    }

    [Fact]
    public void Morning_and_afternoon_are_told_apart_by_the_SLOT_not_the_level()
    {
        // 🔴 Both are Level.Half. The level alone cannot distinguish them, which is exactly why
        // the old capacity scalar could put an afternoon-only volunteer on a 07:00 check-in.
        var morning = For(PreDay, VolunteerAvailabilityLevel.Half, "[Morning 7–12]");
        var afternoon = For(PreDay, VolunteerAvailabilityLevel.Half, "[Afternoon 12–18]");

        Assert.Equal(new TimeWindow(new TimeOnly(7, 0), new TimeOnly(12, 0)), morning.Window);
        Assert.Equal(new TimeWindow(new TimeOnly(12, 0), new TimeOnly(18, 0)), afternoon.Window);
    }

    [Fact]
    public void A_STALE_slot_tag_resolves_to_the_CURRENT_window()
    {
        // 🔴 Two such rows exist in production: "[Morning 9–12]" saved against the pre-day and the
        // main day, whose morning windows are now 7–12 and 6:40–12. Resolve maps them onto the
        // current option by Level fallback.
        //
        // ⚠️ THIS is why nothing may parse hours out of the stored string: doing so would schedule
        // those two people against a window that no longer exists on the form.
        var preDay = For(PreDay, VolunteerAvailabilityLevel.Half, "[Morning 9–12] blocked 13:00 for a session");
        var mainDay = For(MainDay, VolunteerAvailabilityLevel.Half, "[Morning 9–12]");

        Assert.Equal(new TimeOnly(7, 0), preDay.Window!.Value.Start);
        Assert.Equal(new TimeOnly(6, 40), mainDay.Window!.Value.Start);
    }

    [Fact]
    public void Attending_can_help_EVENING_is_an_evening_window_despite_being_Half()
    {
        // 🔴 The trap this test exists for: "Half ⇒ morning or afternoon" would schedule these
        // people into the main-day MORNING — precisely when they are attending the conference.
        // Nobody has picked the option yet, which is why it would go unnoticed until someone does.
        var w = For(MainDay, VolunteerAvailabilityLevel.Half, "[Attending conference — can help evening]");

        Assert.True(w.Present);
        Assert.Equal(new TimeOnly(17, 0), w.Window!.Value.Start);
        Assert.Equal(new TimeOnly(21, 0), w.Window!.Value.End);

        // It covers the 17:30–21:00 pack-down and nothing in the morning.
        Assert.True(w.CanCover(new TimeWindow(new TimeOnly(17, 30), new TimeOnly(21, 0))));
        Assert.False(w.CanCover(new TimeWindow(new TimeOnly(6, 40), new TimeOnly(12, 0))));
    }

    [Fact]
    public void Attending_ONLY_on_the_pre_day_is_not_available_at_all()
    {
        // The pre-day's "attending only — not working" box is Blocked: present, but giving no time.
        var w = For(PreDay, VolunteerAvailabilityLevel.Blocked, "[Attending conference]");

        Assert.False(w.Present);
        Assert.False(w.CanCover(null));
    }

    [Fact]
    public void Not_able_to_help_is_not_available()
    {
        var w = For(SetupDay, VolunteerAvailabilityLevel.Unavailable, "[Not able to help]");

        Assert.False(w.Present);
    }

    // ── The packing day is binary, by operator decision ──────────────────────────────────

    [Fact]
    public void The_packing_day_has_NO_window_so_any_task_on_it_matches()
    {
        // Operator 2026-09-22: *"packing day is a few tasks where people will be there all time ·
        // there are no 50/50 split that day · think of it as full day, either they come or not."*
        //
        // ⚠️ Concretely: the day's own task (*Pack attendee bags*) runs 09:00–15:00 while the day
        // itself was described as 9–14. A 09:00–14:00 window would make that task unfillable by
        // the 17 people who said yes to exactly it.
        var yes = For(PackingDay, VolunteerAvailabilityLevel.Full, "[Yes, I can help]");

        Assert.True(yes.Present);
        Assert.Null(yes.Window);
        Assert.True(yes.CanCover(new TimeWindow(new TimeOnly(9, 0), new TimeOnly(15, 0))));

        var no = For(PackingDay, VolunteerAvailabilityLevel.Unavailable, "[No, I cannot help]");
        Assert.False(no.Present);
    }

    [Fact]
    public void A_day_with_no_configured_hours_stays_unconstrained()
    {
        // Other editions and test fixtures use the generic option set. Inventing a window there
        // would refuse work on days nobody has ever stated hours for.
        var w = For(new DateOnly(2030, 6, 1), VolunteerAvailabilityLevel.Full, null);

        Assert.True(w.Present);
        Assert.Null(w.Window);
    }
}
