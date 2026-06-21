namespace CommunityHub.Core.Domain;

/// <summary>The state of one attendee's Master Class signup.</summary>
public enum MasterClassSignupStatus
{
    /// <summary>Holds a confirmed seat.</summary>
    Confirmed = 0,
    /// <summary>On the waitlist (FIFO by <see cref="MasterClassSignup.CreatedAt"/>).</summary>
    Waitlisted = 1,
    /// <summary>
    /// A seat has opened and is being HELD for this person, who already holds a
    /// confirmed seat in another MC — they must decide to keep their current seat
    /// or switch (give it up to take this one). Counts against capacity while held;
    /// expires (passes to the next waitlisted) at <see cref="MasterClassSignup.OfferExpiresAt"/>.
    /// </summary>
    Offered = 2,
}

/// <summary>
/// One attendee's in-hub Master Class signup (REQUIREMENTS §6) — the CEH-owned
/// replacement for the Zoho Bookings master-class flow. An attendee may hold
/// <b>exactly one</b> signup (a confirmed seat OR a waitlist place), enforced by a
/// unique index on (EventId, AttendeeId). Eligibility (a 2-day Backstage ticket)
/// is checked at signup time. Links the master-class <see cref="Session"/> to the
/// <see cref="Domain.Attendee"/> (NOT a Participant — attendees are not hub users).
/// </summary>
public class MasterClassSignup
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The master-class session signed up for.</summary>
    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    /// <summary>The attendee (2-day-ticket holder).</summary>
    public int AttendeeId { get; set; }
    public Attendee Attendee { get; set; } = null!;

    public MasterClassSignupStatus Status { get; set; } = MasterClassSignupStatus.Waitlisted;

    /// <summary>Created (sign-up) time — also the FIFO key for waitlist ordering.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the seat was confirmed (at signup if room, or on promotion).</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>
    /// When an <see cref="MasterClassSignupStatus.Offered"/> hold expires (3h after
    /// the offer). Past this, the held seat passes to the next waitlisted attendee.
    /// Null unless currently Offered.
    /// </summary>
    public DateTimeOffset? OfferExpiresAt { get; set; }

    /// <summary>When a promotion-to-seat notification was sent (null = not notified).</summary>
    public DateTimeOffset? PromotionNotifiedAt { get; set; }

    /// <summary>
    /// When the attendee accepted, at waitlist signup, that taking this class will
    /// AUTO-CANCEL their current confirmed Master Class (the auto-switch terms).
    /// Required + stamped only when they joined this waitlist while already holding
    /// a seat under an auto-switching mode. Null otherwise.
    /// </summary>
    public DateTimeOffset? AutoSwitchConsentAt { get; set; }

    /// <summary>The attendee opted in to a calendar entry emailed ~1 month before the event.</summary>
    public bool WantsMonthBeforeReminder { get; set; }

    /// <summary>When the ~1-month-before calendar reminder was sent (idempotency; null = not sent).</summary>
    public DateTimeOffset? MonthReminderSentAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
