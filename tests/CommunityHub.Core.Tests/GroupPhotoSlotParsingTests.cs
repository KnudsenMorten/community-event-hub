using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1077 stage 5 — reading the operator's own timeslot list.
/// </summary>
/// <remarks>
/// <para>His 2026 plan looked like <c>25-02-2026&#9;14:35-14:40</c>: day-first dates, a tab, and a
/// time RANGE. A parser that only accepted <c>yyyy-MM-dd HH:mm</c> would have rejected every line
/// of the real data and reported twenty-three mistakes back to him.</para>
///
/// <para>🔴 <b>The day-first test is the one that matters.</b> <c>09-02-2027</c> is 9 February to
/// him and 2 September to an invariant parser — BOTH parse, so "whichever works" would silently
/// schedule the photos seven months late, and the mistake would look like a data-entry error rather
/// than a code decision.</para>
/// </remarks>
public sealed class GroupPhotoSlotParsingTests
{
    [Fact]
    public void A_day_first_date_is_read_as_the_day_not_the_month()
    {
        Assert.True(GroupPhotoScheduleService.TryParseSlotLine("09-02-2027 14:35", out var when, out _));

        Assert.Equal(2027, when.Year);
        Assert.Equal(2, when.Month);      // 🔴 February, not September
        Assert.Equal(9, when.Day);
        Assert.Equal(new TimeSpan(14, 35, 0), when.TimeOfDay);
    }

    /// <summary>His actual line shape: a tab between the date and a time RANGE.</summary>
    [Fact]
    public void His_own_format_parses_and_the_range_sets_the_length()
    {
        Assert.True(GroupPhotoScheduleService.TryParseSlotLine(
            "25-02-2026\t14:35-14:40", out var when, out var minutes));

        Assert.Equal(new DateTime(2026, 2, 25, 14, 35, 0), when);
        Assert.Equal(5, minutes);
    }

    /// <summary>ISO still works — an unambiguous date is unambiguous either way round.</summary>
    [Fact]
    public void An_iso_date_still_parses()
    {
        Assert.True(GroupPhotoScheduleService.TryParseSlotLine("2027-02-10 09:45", out var when, out var minutes));

        Assert.Equal(new DateTime(2027, 2, 10, 9, 45, 0), when);
        Assert.Null(minutes);   // no range given ⇒ the caller's default length applies
    }

    [Theory]
    [InlineData("09-02-2027  09:45-09:50")]   // several spaces
    [InlineData("9-2-2027 09:45-09:50")]      // single digits
    [InlineData("09/02/2027 09:45-09:50")]    // slashes
    public void The_separators_people_actually_type_are_accepted(string line)
    {
        Assert.True(GroupPhotoScheduleService.TryParseSlotLine(line, out var when, out var minutes));
        Assert.Equal(new DateTime(2027, 2, 9, 9, 45, 0), when);
        Assert.Equal(5, minutes);
    }

    /// <summary>
    /// ⚠️ A range that ends before it starts is not trusted for the length — the slot is still read,
    /// but the caller's default applies rather than storing something that ends before it begins.
    /// </summary>
    [Fact]
    public void A_backwards_range_does_not_become_a_negative_length()
    {
        Assert.True(GroupPhotoScheduleService.TryParseSlotLine("09-02-2027 14:40-14:35", out _, out var minutes));
        Assert.Null(minutes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("09-02-2027")]          // a date with no time is not a slot
    [InlineData("not a date 09:45")]
    public void Rubbish_is_refused_so_the_line_can_be_reported(string line)
        => Assert.False(GroupPhotoScheduleService.TryParseSlotLine(line, out _, out _));
}
