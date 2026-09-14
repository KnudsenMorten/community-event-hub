using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Xunit;
using Cell = CommunityHub.Core.Volunteers.VolunteerAvailabilityGrid.Cell;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1146 — the green/red half-day grid he preselects volunteers from.
///
/// <para>Operator 2026-08-28: <i>"with colos like GREEN, RED, so we have a quick overview per day
/// (morning,evening). If a person selected full day, then it is green in both fields."</i></para>
///
/// <para>🔑 <b>Every case here is decided by the stored SLOT, not by the level.</b> Morning and
/// Afternoon share <c>Level.Half</c>, and so does the main-day evening offer — so a grid derived from
/// the level alone would paint three different answers identically. That is why the slot is
/// persisted, and this is the test that says so.</para>
/// </summary>
public class VolunteerAvailabilityGridTests
{
    // The real ELDK27 days, from VolunteerDayOptions.
    private static readonly DateOnly MonSetup = new(2027, 2, 8);
    private static readonly DateOnly PreDay = new(2027, 2, 9);
    private static readonly DateOnly MainDay = new(2027, 2, 10);
    private static readonly DateOnly PackingDay = new(2027, 2, 7);

    private static VolunteerAvailabilityGrid.DayCells Cells(
        DateOnly day, VolunteerAvailabilityLevel level, string slot) =>
        VolunteerAvailabilityGrid.CellsFor(day, level, $"[{slot}]");

    /// <summary>🔒 HIS RULE, stated in his own words.</summary>
    [Theory]
    [InlineData(2027, 2, 8)]
    [InlineData(2027, 2, 9)]
    [InlineData(2027, 2, 10)]
    public void FULL_DAY_is_green_in_BOTH_fields(int y, int m, int d)
    {
        var cells = Cells(new DateOnly(y, m, d), VolunteerAvailabilityLevel.Full, "Full day");

        Assert.Equal(Cell.Available, cells.Morning);
        Assert.Equal(Cell.Available, cells.Afternoon);
    }

    [Fact]
    public void MORNING_is_green_in_the_morning_only()
    {
        var cells = Cells(PreDay, VolunteerAvailabilityLevel.Half, "Morning 7–12");

        Assert.Equal(Cell.Available, cells.Morning);
        Assert.Equal(Cell.Unavailable, cells.Afternoon);
        Assert.True(cells.MorningCounts);
        Assert.False(cells.AfternoonCounts);
    }

    [Fact]
    public void AFTERNOON_is_green_in_the_afternoon_only()
    {
        var cells = Cells(MonSetup, VolunteerAvailabilityLevel.Half, "Afternoon 12–17");

        Assert.Equal(Cell.Unavailable, cells.Morning);
        Assert.Equal(Cell.Available, cells.Afternoon);
    }

    /// <summary>
    /// 🔴 The case a level-based grid gets WRONG, and dangerously so.
    /// </summary>
    /// <remarks>
    /// The main-day "attending, can help in the evening" option is <c>Level.Half</c> — the same level
    /// as Morning and Afternoon — because §1138 deliberately kept it that way ("they ARE giving
    /// time"). Painting it green would put someone on a 12–17 shift they explicitly declined;
    /// painting it red would hide a real offer of help. It gets its own state.
    /// </remarks>
    [Fact]
    public void ATTENDING_but_can_help_in_the_evening_is_neither_green_nor_red()
    {
        var cells = Cells(
            MainDay, VolunteerAvailabilityLevel.Half, "Attending conference — can help evening");

        Assert.Equal(Cell.Unavailable, cells.Morning);
        Assert.Equal(Cell.EveningOnly, cells.Afternoon);

        // 🔒 It must NOT inflate the afternoon head-count he staffs the 12–17 shift from.
        Assert.False(cells.AfternoonCounts);
    }

    [Fact]
    public void ATTENDING_only_is_red_in_both()
    {
        var cells = Cells(PreDay, VolunteerAvailabilityLevel.Blocked, "Attending conference");

        Assert.Equal(Cell.Unavailable, cells.Morning);
        Assert.Equal(Cell.Unavailable, cells.Afternoon);
    }

    [Fact]
    public void NOT_ABLE_TO_HELP_is_red_in_both()
    {
        var cells = Cells(MainDay, VolunteerAvailabilityLevel.Unavailable, "Not able to help");

        Assert.Equal(Cell.Unavailable, cells.Morning);
        Assert.Equal(Cell.Unavailable, cells.Afternoon);
    }

    /// <summary>
    /// 🔒 An UNANSWERED day is not a refusal.
    /// </summary>
    /// <remarks>
    /// Painting it red would tell him he has been turned down by people who were never asked — and
    /// this page exists for him to decide who to chase.
    /// </remarks>
    [Fact]
    public void A_day_with_NO_answer_is_unknown_not_red()
    {
        var cells = VolunteerAvailabilityGrid.CellsFor(
            MainDay, VolunteerAvailabilityLevel.Blocked, null, hasAnswer: false);

        Assert.Equal(Cell.Unknown, cells.Morning);
        Assert.Equal(Cell.Unknown, cells.Afternoon);
        Assert.False(cells.MorningCounts);
        Assert.False(cells.AfternoonCounts);
    }

    /// <summary>The packing day's "Yes, I can help" is exclusive+Full — both halves.</summary>
    [Fact]
    public void The_packing_days_YES_covers_both_halves()
    {
        var cells = Cells(PackingDay, VolunteerAvailabilityLevel.Full, "Yes, I can help");

        Assert.Equal(Cell.Available, cells.Morning);
        Assert.Equal(Cell.Available, cells.Afternoon);
    }

    [Fact]
    public void The_packing_days_NO_is_red_in_both()
    {
        var cells = Cells(PackingDay, VolunteerAvailabilityLevel.Unavailable, "No, I cannot help");

        Assert.Equal(Cell.Unavailable, cells.Morning);
        Assert.Equal(Cell.Unavailable, cells.Afternoon);
    }

    /// <summary>
    /// The generic fallback set's plain "Half day" says WHICH half nowhere, so neither cell may
    /// claim one — a guessed half is a missed shift.
    /// </summary>
    [Fact]
    public void A_generic_HALF_DAY_does_not_guess_which_half()
    {
        var cells = Cells(new DateOnly(2030, 5, 5), VolunteerAvailabilityLevel.Half, "Half day");

        Assert.Equal(Cell.Unknown, cells.Morning);
        Assert.Equal(Cell.Unknown, cells.Afternoon);
    }

    [Fact]
    public void An_answer_whose_slot_no_longer_exists_still_resolves_by_level()
    {
        // A volunteer who saved "[Morning 9–12]" before §1134 changed the pre-day hours. Resolve
        // falls back to the first option of that level, which is Morning on every curated day.
        var cells = Cells(PreDay, VolunteerAvailabilityLevel.Half, "Morning 9–12");

        Assert.Equal(Cell.Available, cells.Morning);
        Assert.Equal(Cell.Unavailable, cells.Afternoon);
    }

    [Fact]
    public void Every_cell_has_a_colour_and_a_label()
    {
        foreach (var cell in Enum.GetValues<Cell>())
        {
            Assert.False(string.IsNullOrWhiteSpace(VolunteerAvailabilityGrid.CssClass(cell)));
            Assert.False(string.IsNullOrWhiteSpace(VolunteerAvailabilityGrid.Label(cell)));
        }
    }
}
