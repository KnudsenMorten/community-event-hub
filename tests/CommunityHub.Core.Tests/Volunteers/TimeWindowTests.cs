using CommunityHub.Core.Volunteers;
using Xunit;

namespace CommunityHub.Core.Tests.Volunteers;

/// <summary>
/// §1297 — the planner's time primitive. Small, but every scheduling decision is built on it,
/// so the two rules that are easy to get backwards are pinned here rather than inferred from
/// the matcher's behaviour.
/// </summary>
public sealed class TimeWindowTests
{
    private static TimeWindow W(int h1, int m1, int h2, int m2) =>
        new(new TimeOnly(h1, m1), new TimeOnly(h2, m2));

    [Fact]
    public void Back_to_back_shifts_do_NOT_overlap()
    {
        // 🔴 THE reason the interval is half-open. 09:00–12:00 and 12:00–15:00 is the commonest
        // pair in the plan; a closed interval would collide at 12:00 and the planner would refuse
        // to give one person two consecutive shifts — i.e. most of the day.
        Assert.False(W(9, 0, 12, 0).Overlaps(W(12, 0, 15, 0)));
        Assert.False(W(12, 0, 15, 0).Overlaps(W(9, 0, 12, 0)));
    }

    [Fact]
    public void A_shared_minute_IS_an_overlap()
    {
        Assert.True(W(9, 0, 12, 0).Overlaps(W(11, 59, 13, 0)));
        Assert.True(W(9, 0, 17, 0).Overlaps(W(10, 0, 11, 0)));  // fully enclosed
    }

    [Fact]
    public void Containment_requires_the_WHOLE_task_to_fit()
    {
        // 🔑 Eligibility is containment, not overlap: a volunteer free 09:00–12:00 cannot do a
        // task that runs to 13:00, even though the two overlap for three hours. Someone who has
        // to leave mid-task has not covered it.
        var morning = W(9, 0, 12, 0);

        Assert.True(morning.Contains(W(9, 0, 12, 0)));   // exact fit
        Assert.True(morning.Contains(W(10, 0, 11, 0)));
        Assert.False(morning.Contains(W(11, 0, 13, 0))); // runs past the end
        Assert.False(morning.Contains(W(8, 30, 10, 0))); // starts before
    }

    [Fact]
    public void Hours_is_the_duration_used_for_fairness()
    {
        Assert.Equal(3d, W(9, 0, 12, 0).Hours);
        Assert.Equal(5d + 20d / 60d, W(6, 40, 12, 0).Hours, 6);
    }

    [Fact]
    public void A_window_that_does_not_move_forward_is_rejected()
    {
        // ⚠️ A zero-length or inverted shift is a DATA error. Silently treating it as an empty
        // window would make it a task nobody ever has to staff, which is the opposite of useful.
        Assert.Throws<ArgumentException>(() => W(12, 0, 12, 0));
        Assert.Throws<ArgumentException>(() => W(15, 0, 9, 0));
    }

    [Fact]
    public void TryCreate_returns_null_instead_of_throwing_for_unusable_input()
    {
        Assert.Null(TimeWindow.TryCreate(new TimeOnly(9, 0), null));
        Assert.Null(TimeWindow.TryCreate(null, new TimeOnly(9, 0)));
        Assert.Null(TimeWindow.TryCreate(new TimeOnly(12, 0), new TimeOnly(12, 0)));
        Assert.NotNull(TimeWindow.TryCreate(new TimeOnly(9, 0), new TimeOnly(12, 0)));
    }

    // ── DayWindow — what an availability answer covers ───────────────────────────────────

    [Fact]
    public void An_absent_volunteer_covers_nothing()
    {
        Assert.False(DayWindow.Absent.CanCover(W(9, 0, 12, 0)));
        Assert.False(DayWindow.Absent.CanCover(null));
    }

    [Fact]
    public void Present_with_no_stated_hours_covers_anything_that_day()
    {
        // 🔑 Null is a real answer, not a missing one: the packing day is binary ("either they
        // come or not") and the generic option set has no configured hours. Inventing a window
        // for either would refuse work those people did volunteer for.
        Assert.True(DayWindow.AllDay.CanCover(W(9, 0, 15, 0)));
        Assert.True(DayWindow.AllDay.CanCover(null));
    }

    [Fact]
    public void A_stated_window_covers_only_what_fits_inside_it()
    {
        var afternoon = new DayWindow(true, W(12, 0, 17, 0));

        Assert.True(afternoon.CanCover(W(13, 0, 15, 0)));
        Assert.False(afternoon.CanCover(W(11, 0, 13, 0)));
        // A task with no stated hours falls back to "is this person here that day at all".
        Assert.True(afternoon.CanCover(null));
    }
}
