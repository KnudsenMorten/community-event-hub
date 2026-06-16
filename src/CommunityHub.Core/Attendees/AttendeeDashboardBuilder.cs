using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Attendees;

/// <summary>
/// Practical, edition-level facts an attendee needs at a glance. Sourced from
/// the active <see cref="Event"/> row (dates / venue) so it is multi-edition
/// with no per-year code. Plain data so the builder stays unit-testable.
/// </summary>
public sealed record EventPracticalInfo(
    string EventDisplayName,
    string CommunityName,
    string VenueName,
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly? PreDayDate);

/// <summary>
/// How prominent the attendee's Master Class status is on the dashboard.
/// </summary>
public enum AgendaStatusTone
{
    /// <summary>All good (e.g. a single confirmed booking).</summary>
    Ok = 0,

    /// <summary>Something needs the attendee's attention (no booking / duplicate).</summary>
    Action = 1,

    /// <summary>Neutral / informational (no record yet).</summary>
    Info = 2
}

/// <summary>
/// The computed "My Event" attendee dashboard view model. Built by
/// <see cref="AttendeeDashboardBuilder"/> from the attendee's reconciled
/// <see cref="Attendee"/> record (may be null), the edition's practical info,
/// and "now" - all explicit inputs so it is fully unit-testable without a
/// database or a clock.
/// </summary>
public sealed class MyEventDashboard
{
    // --- Practical info -----------------------------------------------------
    public string EventDisplayName { get; init; } = string.Empty;
    public string CommunityName { get; init; } = string.Empty;
    public string VenueName { get; init; } = string.Empty;
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public DateOnly? PreDayDate { get; init; }

    /// <summary>Whole days until the event's first day (pre-day if present); 0 on/after; negative is clamped to 0.</summary>
    public int DaysUntilStart { get; init; }

    /// <summary>True when "now" falls within the event window (pre-day .. end day inclusive).</summary>
    public bool IsEventLive { get; init; }

    // --- Master Class status (a one-line agenda summary) --------------------
    public bool HasRecord { get; init; }
    public string AgendaHeadline { get; init; } = string.Empty;
    public string? MasterClassName { get; init; }
    public AgendaStatusTone AgendaTone { get; init; }

    // --- Self check-in ------------------------------------------------------
    /// <summary>True once a check-in window is open (on a confirmed attendee, during the event window).</summary>
    public bool CanCheckIn { get; init; }

    /// <summary>True if the attendee has already self-checked-in.</summary>
    public bool IsCheckedIn { get; init; }

    /// <summary>When they checked in, for display. Null if not checked in.</summary>
    public DateTimeOffset? CheckedInAt { get; init; }

    /// <summary>Short reason the check-in button is hidden (when <see cref="CanCheckIn"/> is false and not yet checked in).</summary>
    public string? CheckInUnavailableReason { get; init; }
}

/// <summary>
/// Builds the attendee "My Event" dashboard from explicit inputs. Pure: no
/// DbContext, no <c>DateTime.Now</c> - "now" is passed in - so the date
/// windows (countdown, event-live, check-in eligibility) are unit-testable.
/// </summary>
public static class AttendeeDashboardBuilder
{
    /// <summary>
    /// Compute the dashboard. <paramref name="record"/> may be null when no
    /// ticket/booking has been reconciled for the attendee yet.
    /// </summary>
    public static MyEventDashboard Build(
        Attendee? record,
        EventPracticalInfo info,
        DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // The event "opens" on the pre-day when there is one, else the first day.
        var firstDay = info.PreDayDate is { } pd && pd < info.StartDate ? pd : info.StartDate;

        int daysUntil = firstDay.DayNumber - today.DayNumber;
        if (daysUntil < 0) daysUntil = 0;

        bool isLive = today >= firstDay && today <= info.EndDate;

        // --- Master Class status line -----------------------------------
        bool hasRecord = record is not null;
        string headline;
        AgendaStatusTone tone;
        string? mcName = record?.MasterClassName;

        if (record is null)
        {
            headline = "We have no ticket or booking on file for you yet.";
            tone = AgendaStatusTone.Info;
        }
        else
        {
            switch (record.BookingStatus)
            {
                case MasterClassBookingStatus.Booked:
                    headline = "Your Master Class seat is reserved.";
                    tone = AgendaStatusTone.Ok;
                    break;
                case MasterClassBookingStatus.MultipleBookings:
                    headline = "You have more than one Master Class booking - please keep one and cancel the others.";
                    tone = AgendaStatusTone.Action;
                    break;
                default:
                    headline = "You have not reserved a Master Class seat yet.";
                    tone = AgendaStatusTone.Action;
                    break;
            }
        }

        // --- Self check-in window ---------------------------------------
        // Eligible only when: we have a record, the event is live, and the
        // attendee actually holds a ticket (not None). Already-checked-in
        // short-circuits the window so the confirmation always shows.
        bool isCheckedIn = record?.CheckedInAt is not null;
        bool hasTicket = record is { TicketStatus: not TicketStatus.None };
        bool canCheckIn = false;
        string? unavailable = null;

        if (!isCheckedIn)
        {
            if (record is null)
            {
                unavailable = "We have no attendee record for you yet.";
            }
            else if (!hasTicket)
            {
                unavailable = "Check-in opens for ticket holders.";
            }
            else if (!isLive)
            {
                unavailable = daysUntil > 0
                    ? $"Check-in opens when the event starts ({firstDay:dd MMM yyyy})."
                    : "Check-in is closed - the event has ended.";
            }
            else
            {
                canCheckIn = true;
            }
        }

        return new MyEventDashboard
        {
            EventDisplayName = info.EventDisplayName,
            CommunityName = info.CommunityName,
            VenueName = info.VenueName,
            StartDate = info.StartDate,
            EndDate = info.EndDate,
            PreDayDate = info.PreDayDate,
            DaysUntilStart = daysUntil,
            IsEventLive = isLive,

            HasRecord = hasRecord,
            AgendaHeadline = headline,
            MasterClassName = mcName,
            AgendaTone = tone,

            CanCheckIn = canCheckIn,
            IsCheckedIn = isCheckedIn,
            CheckedInAt = record?.CheckedInAt,
            CheckInUnavailableReason = unavailable,
        };
    }
}
