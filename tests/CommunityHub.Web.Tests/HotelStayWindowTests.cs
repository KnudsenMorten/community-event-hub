using CommunityHub.Core.Config;
using CommunityHub.Forms.Steps;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1135 — the hotel picker accepts the nights people actually arrive on.
///
/// <para>Operator 2026-08-25: <i>"we have people checking in on 5th feb, 6th, 7th, 8th, 9th feb and
/// checkout 10th, 11th or 12 th feb"</i> · <i>"but hotel is wrong then"</i> · and the concern that
/// matters most: <i>"we have people that have signed up already with specific dates, so you cannot
/// just change this without loosing all dates"</i> · <i>"critical, we are live now with live
/// data"</i>.</para>
///
/// <para>🔴 The old window was DERIVED as <c>preDay − 2 … day2 + 2</c> = <b>06–12 Feb</b>, so the 5th
/// was outside the picker and an early arrival could not book that night at all. Nothing failed —
/// the input simply refused the date, which reads as "not allowed" rather than "misconfigured".</para>
/// </summary>
public sealed class HotelStayWindowTests
{
    private static EditionDates Eldk27() => new()
    {
        PreDay = "2027-02-08",
        Day1 = "2027-02-09",
        Day2 = "2027-02-10",
        HotelStayFrom = "2027-02-05",
        HotelStayUntil = "2027-02-12",
    };

    // ── The window he actually needs ─────────────────────────────────────────────────────

    [Fact]
    public void The_stated_window_is_used_verbatim()
    {
        var (start, end) = HotelFormService.StayWindowFor(Eldk27());

        Assert.Equal(new DateOnly(2027, 2, 5), start);
        Assert.Equal(new DateOnly(2027, 2, 12), end);
    }

    [Theory]
    [InlineData(2, 5)]   // ⇐ the night the OLD derivation excluded
    [InlineData(2, 6)]
    [InlineData(2, 7)]
    [InlineData(2, 8)]
    [InlineData(2, 9)]
    public void Every_check_in_night_he_named_is_inside_the_window(int m, int d)
    {
        var (start, end) = HotelFormService.StayWindowFor(Eldk27());
        var day = new DateOnly(2027, m, d);

        Assert.True(day >= start && day <= end, $"{day:yyyy-MM-dd} must be selectable");
    }

    [Theory]
    [InlineData(2, 10)]
    [InlineData(2, 11)]
    [InlineData(2, 12)]
    public void Every_check_out_day_he_named_is_inside_the_window(int m, int d)
    {
        var (start, end) = HotelFormService.StayWindowFor(Eldk27());
        var day = new DateOnly(2027, m, d);

        Assert.True(day >= start && day <= end, $"{day:yyyy-MM-dd} must be selectable");
    }

    [Fact]
    public void The_new_window_is_a_SUPERSET_of_the_old_one()
    {
        // 🔴 THE "we are live with live data" GUARANTEE, in one assertion.
        //
        // The old derivation gave 06–12 Feb. Widening to 05–12 means every date that was bookable
        // before is STILL bookable, so no existing booking can fall outside the picker and be
        // rejected on the owner's next save. A NARROWER window is the dangerous direction, and this
        // test fails if anyone ever introduces one.
        var (oldStart, oldEnd) = HotelFormService.StayWindowFor(new EditionDates
        {
            PreDay = "2027-02-08", Day1 = "2027-02-09", Day2 = "2027-02-10",
        });
        var (newStart, newEnd) = HotelFormService.StayWindowFor(Eldk27());

        Assert.Equal(new DateOnly(2027, 2, 6), oldStart);   // what it used to be
        Assert.Equal(new DateOnly(2027, 2, 12), oldEnd);

        Assert.True(newStart <= oldStart, "the window must not start LATER than before");
        Assert.True(newEnd >= oldEnd, "the window must not end EARLIER than before");
    }

    // ── The fallback must be untouched for every other edition ───────────────────────────

    [Fact]
    public void An_edition_that_states_nothing_keeps_the_old_derivation()
    {
        var (start, end) = HotelFormService.StayWindowFor(new EditionDates
        {
            PreDay = "2030-05-10", Day1 = "2030-05-11", Day2 = "2030-05-12",
        });

        Assert.Equal(new DateOnly(2030, 5, 8), start);   // preDay − 2
        Assert.Equal(new DateOnly(2030, 5, 14), end);    // day2 + 2
    }

    [Theory]
    [InlineData("", "2027-02-12")]              // only one stated
    [InlineData("2027-02-05", "")]
    [InlineData("not-a-date", "2027-02-12")]    // unparseable
    [InlineData("2027-02-12", "2027-02-05")]    // reversed — until <= from
    public void A_half_stated_or_nonsense_window_falls_back_rather_than_guessing(string from, string until)
    {
        // 🔒 Fail-soft, the §403 contract: a WRONG bound blocks a real booking, so a broken pair
        // must not be honoured. The derivation is a known-sane answer; half a config is not.
        var (start, end) = HotelFormService.StayWindowFor(new EditionDates
        {
            PreDay = "2027-02-08", Day1 = "2027-02-09", Day2 = "2027-02-10",
            HotelStayFrom = from, HotelStayUntil = until,
        });

        Assert.Equal(new DateOnly(2027, 2, 6), start);
        Assert.Equal(new DateOnly(2027, 2, 12), end);
    }

    // ── The prefill is unchanged, and only ever fills a blank ────────────────────────────

    [Fact]
    public void The_prefilled_dates_are_unchanged_by_this()
    {
        // §425's prefill reads day1/day2 and is deliberately NOT affected: it is what someone who
        // has never booked sees, and moving it would change the answer most people accept.
        var (checkIn, checkOut) = HotelFormService.DefaultStayFor(Eldk27());

        Assert.Equal(new DateOnly(2027, 2, 9), checkIn);
        Assert.Equal(new DateOnly(2027, 2, 10), checkOut);
    }
}
