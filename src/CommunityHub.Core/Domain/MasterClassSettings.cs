namespace CommunityHub.Core.Domain;

/// <summary>
/// How a freed Master Class seat is handed to a waitlisted attendee who ALREADY
/// holds a confirmed seat in another MC (a person can never hold two confirmed).
/// Organizer-selectable (REQUIREMENTS §6).
/// </summary>
public enum MasterClassPromotionMode
{
    /// <summary>
    /// Default. The freed seat is OFFERED (held for the configured hours) and the
    /// attendee decides — keep their current seat, or give it up to switch.
    /// </summary>
    OfferAndDecide = 0,

    /// <summary>
    /// Auto-switch: the attendee is moved into the freed seat and their previous
    /// seat is automatically released (which promotes that MC's waitlist). No hold.
    /// </summary>
    AutoSwitch = 1,

    /// <summary>
    /// Skip: an attendee who already holds a seat is passed over; the seat goes to
    /// the first waitlisted attendee who holds no seat. No hold, no switch.
    /// </summary>
    Skip = 2,
}

/// <summary>
/// Per-edition Master Class signup settings the organizer controls (REQUIREMENTS §6):
/// how long a held offer waits for a decision, and which promotion mode applies when
/// the next waitlisted attendee already holds a seat. No row ⇒ the shipped defaults.
/// </summary>
public class MasterClassSettings
{
    public int Id { get; set; }

    /// <summary>Edition scope (unique).</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Hours a freed seat is held for an attendee to accept/decline (default 12).</summary>
    public int OfferHoldHours { get; set; } = 12;

    /// <summary>
    /// What happens when the promoted attendee already holds a seat. <b>Default =
    /// AutoSwitch</b> (operator): take the new seat, release the old. With
    /// OfferAndDecide the seat is held for <see cref="OfferHoldHours"/> and, if the
    /// attendee doesn't choose, it <b>defaults to auto-switch</b> (option a) on expiry.
    /// </summary>
    public MasterClassPromotionMode PromotionMode { get; set; } = MasterClassPromotionMode.AutoSwitch;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedByEmail { get; set; }
}
