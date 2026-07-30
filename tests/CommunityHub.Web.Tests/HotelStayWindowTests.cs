using CommunityHub.Core.Config;
using CommunityHub.Forms.Steps;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §403 — the hotel date picker opened on TODAY (operator 2026-07-26: <i>"bug - i dont see year -
/// can it default to feb 9, 2027 instead of taking the dates today"</i>), so he was looking at July
/// 2026 while booking for a February 2027 event.
///
/// <para>The fix is a <c>min</c>/<c>max</c> derived from the edition's own dates: a browser opens an
/// empty <c>&lt;input type="date"&gt;</c> at <c>min</c> when <c>min</c> lies in the future. These
/// tests pin the derivation, because the failure mode of getting it WRONG is not a cosmetic one —
/// a bound that is too tight silently blocks a legitimate booking, and the person would just see
/// the date refuse to take.</para>
/// </summary>
public sealed class HotelStayWindowTests
{
    [Fact]
    public void The_window_brackets_the_event_with_two_nights_either_side()
    {
        // The real ELDK27 dates.
        var (start, end) = HotelFormService.StayWindowFor(new EditionDates
        {
            PreDay = "2027-02-08", Day1 = "2027-02-09", Day2 = "2027-02-10",
        });

        // Padding is deliberately generous: someone flying in from further away arrives the night
        // before setup, and a late flight home means a night after day 2. Bounding tightly to the
        // three event days would BLOCK a real booking — a far worse failure than a picker opening
        // on the wrong month, which is all this exists to fix.
        Assert.Equal(new DateOnly(2027, 2, 6), start);
        Assert.Equal(new DateOnly(2027, 2, 12), end);
    }

    [Fact]
    public void The_window_opens_in_the_EVENT_month_not_the_current_one()
    {
        // The whole point, stated as the operator would: whatever month it is today, the picker's
        // lower bound sits in the event's month — that is what moves the calendar.
        var (start, _) = HotelFormService.StayWindowFor(new EditionDates
        {
            PreDay = "2027-02-08", Day1 = "2027-02-09", Day2 = "2027-02-10",
        });

        Assert.Equal(2027, start!.Value.Year);
        Assert.Equal(2, start.Value.Month);
    }

    [Fact]
    public void A_one_day_edition_falls_back_to_day1_then_to_the_pre_day()
    {
        var (_, endFromDay1) = HotelFormService.StayWindowFor(new EditionDates
        {
            PreDay = "2027-02-08", Day1 = "2027-02-09", Day2 = "",
        });
        Assert.Equal(new DateOnly(2027, 2, 11), endFromDay1);

        var (start, endFromPreDay) = HotelFormService.StayWindowFor(new EditionDates
        {
            PreDay = "2027-02-08",
        });
        Assert.Equal(new DateOnly(2027, 2, 6), start);
        Assert.Equal(new DateOnly(2027, 2, 10), endFromPreDay);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    [InlineData("2027-13-45")]
    public void An_unusable_pre_day_yields_NO_bound_rather_than_a_guess(string? preDay)
    {
        // Deliberate direction. With no bound the picker simply behaves as it always did (opening on
        // today) — mildly annoying. With a GUESSED bound it could refuse the very night someone
        // needs, and they would have no way to tell why. Degrade to the old behaviour, never to a
        // wrong rule.
        var (start, end) = HotelFormService.StayWindowFor(new EditionDates { PreDay = preDay! });

        Assert.Null(start);
        Assert.Null(end);
    }

    [Fact]
    public void No_dates_block_at_all_yields_no_bound()
    {
        var (start, end) = HotelFormService.StayWindowFor(null);
        Assert.Null(start);
        Assert.Null(end);
    }
}
