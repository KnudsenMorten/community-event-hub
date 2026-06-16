using CommunityHub.Core.Attendees;
using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="AttendeeDashboardBuilder"/> - the pure
/// view-model behind the attendee "My Event" dashboard. "now" and the edition
/// dates are explicit inputs, so the countdown / event-live / check-in windows
/// are deterministic without a clock or a database.
/// </summary>
public sealed class AttendeeDashboardBuilderTests
{
    // ELDK27-shaped edition: pre-day 8 Feb, conference 9-10 Feb 2027.
    private static EventPracticalInfo Edition() => new(
        EventDisplayName: "Test Conf 2027",
        CommunityName: "Test Community",
        VenueName: "Test Venue",
        StartDate: new DateOnly(2027, 2, 9),
        EndDate: new DateOnly(2027, 2, 10),
        PreDayDate: new DateOnly(2027, 2, 8));

    private static DateTimeOffset On(int y, int m, int day) => new(new DateTime(y, m, day, 12, 0, 0, DateTimeKind.Utc));

    private static Attendee TicketHolder(MasterClassBookingStatus booking, DateTimeOffset? checkedIn = null) => new()
    {
        EventId = 1,
        Email = "a@example.test",
        TicketStatus = TicketStatus.TwoDay,
        TicketClassName = "2-day",
        BookingStatus = booking,
        MasterClassName = booking == MasterClassBookingStatus.NotBooked ? null : "AI Master Class",
        CheckedInAt = checkedIn,
    };

    // --- Countdown / live window -------------------------------------------

    [Fact]
    public void Counts_down_to_the_pre_day_when_one_exists()
    {
        var d = AttendeeDashboardBuilder.Build(null, Edition(), On(2027, 2, 1));

        // Pre-day is 8 Feb -> 7 days from 1 Feb.
        Assert.Equal(7, d.DaysUntilStart);
        Assert.False(d.IsEventLive);
    }

    [Fact]
    public void Event_is_live_on_the_pre_day()
    {
        var d = AttendeeDashboardBuilder.Build(null, Edition(), On(2027, 2, 8));

        Assert.True(d.IsEventLive);
        Assert.Equal(0, d.DaysUntilStart);
    }

    [Fact]
    public void Event_is_live_on_the_last_day_but_not_after()
    {
        Assert.True(AttendeeDashboardBuilder.Build(null, Edition(), On(2027, 2, 10)).IsEventLive);
        Assert.False(AttendeeDashboardBuilder.Build(null, Edition(), On(2027, 2, 11)).IsEventLive);
    }

    [Fact]
    public void Days_until_never_goes_negative_after_the_event()
    {
        var d = AttendeeDashboardBuilder.Build(null, Edition(), On(2027, 3, 1));
        Assert.Equal(0, d.DaysUntilStart);
    }

    // --- Master Class status tone ------------------------------------------

    [Fact]
    public void No_record_is_informational_and_blocks_check_in()
    {
        var d = AttendeeDashboardBuilder.Build(null, Edition(), On(2027, 2, 9));

        Assert.False(d.HasRecord);
        Assert.Equal(AgendaStatusTone.Info, d.AgendaTone);
        Assert.False(d.CanCheckIn);
        Assert.False(d.IsCheckedIn);
        Assert.NotNull(d.CheckInUnavailableReason);
    }

    [Fact]
    public void Booked_seat_is_ok_tone_and_surfaces_the_session_name()
    {
        var d = AttendeeDashboardBuilder.Build(
            TicketHolder(MasterClassBookingStatus.Booked), Edition(), On(2027, 2, 1));

        Assert.Equal(AgendaStatusTone.Ok, d.AgendaTone);
        Assert.Equal("AI Master Class", d.MasterClassName);
    }

    [Fact]
    public void No_booking_is_action_tone()
    {
        var d = AttendeeDashboardBuilder.Build(
            TicketHolder(MasterClassBookingStatus.NotBooked), Edition(), On(2027, 2, 1));
        Assert.Equal(AgendaStatusTone.Action, d.AgendaTone);
    }

    [Fact]
    public void Multiple_bookings_is_action_tone()
    {
        var d = AttendeeDashboardBuilder.Build(
            TicketHolder(MasterClassBookingStatus.MultipleBookings), Edition(), On(2027, 2, 1));
        Assert.Equal(AgendaStatusTone.Action, d.AgendaTone);
    }

    // --- Self check-in window ----------------------------------------------

    [Fact]
    public void Check_in_is_closed_before_the_event_even_for_a_ticket_holder()
    {
        var d = AttendeeDashboardBuilder.Build(
            TicketHolder(MasterClassBookingStatus.Booked), Edition(), On(2027, 2, 1));

        Assert.False(d.CanCheckIn);
        Assert.Contains("opens when the event starts", d.CheckInUnavailableReason);
    }

    [Fact]
    public void Ticket_holder_can_check_in_during_the_event()
    {
        var d = AttendeeDashboardBuilder.Build(
            TicketHolder(MasterClassBookingStatus.Booked), Edition(), On(2027, 2, 9));

        Assert.True(d.CanCheckIn);
        Assert.False(d.IsCheckedIn);
        Assert.Null(d.CheckInUnavailableReason);
    }

    [Fact]
    public void Already_checked_in_short_circuits_the_window()
    {
        var stamp = On(2027, 2, 9);
        var d = AttendeeDashboardBuilder.Build(
            TicketHolder(MasterClassBookingStatus.Booked, checkedIn: stamp), Edition(), On(2027, 2, 9));

        Assert.True(d.IsCheckedIn);
        Assert.False(d.CanCheckIn); // no re-check-in offered
        Assert.Equal(stamp, d.CheckedInAt);
    }

    [Fact]
    public void Non_ticket_holder_cannot_check_in_even_when_live()
    {
        var rec = TicketHolder(MasterClassBookingStatus.NotBooked);
        rec.TicketStatus = TicketStatus.None;

        var d = AttendeeDashboardBuilder.Build(rec, Edition(), On(2027, 2, 9));

        Assert.False(d.CanCheckIn);
        Assert.Contains("ticket holders", d.CheckInUnavailableReason);
    }

    [Fact]
    public void Check_in_is_closed_after_the_event()
    {
        var d = AttendeeDashboardBuilder.Build(
            TicketHolder(MasterClassBookingStatus.Booked), Edition(), On(2027, 2, 20));

        Assert.False(d.CanCheckIn);
        Assert.Contains("event has ended", d.CheckInUnavailableReason);
    }

    [Fact]
    public void Works_for_an_edition_with_no_pre_day()
    {
        var info = Edition() with { PreDayDate = null };
        // First day is now 9 Feb. On 9 Feb it's live; on 8 Feb it is not.
        Assert.True(AttendeeDashboardBuilder.Build(null, info, On(2027, 2, 9)).IsEventLive);
        Assert.False(AttendeeDashboardBuilder.Build(null, info, On(2027, 2, 8)).IsEventLive);
        Assert.Equal(1, AttendeeDashboardBuilder.Build(null, info, On(2027, 2, 8)).DaysUntilStart);
    }
}
