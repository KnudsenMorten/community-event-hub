namespace CommunityHub.Core.Domain;

/// <summary>
/// <b>RESERVED / UNUSED (§234 4)</b> — the live promotion engine (§93,
/// <c>MasterClassSignupService.PromoteNextAsync</c>) ALWAYS auto-switches and never
/// reads this setting; there is no organizer UI offering it. Kept because the value is
/// persisted in <see cref="MasterClassSettings"/> rows. Original meaning: how a freed
/// Master Class seat is handed to a waitlisted attendee who ALREADY holds a confirmed
/// seat in another MC (a person can never hold two confirmed).
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
/// Per-edition Master Class signup settings (REQUIREMENTS §6). <b>RESERVED / UNUSED
/// (§234 4):</b> both knobs belong to the retired offer/decide model — the live engine
/// always auto-switches, no organizer UI exposes them, and no live path reads them.
/// The table is kept because rows may exist; do not surface these in settings UI.
/// </summary>
public class MasterClassSettings
{
    public int Id { get; set; }

    /// <summary>Edition scope (unique).</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>RESERVED (§234 4): hours a freed seat WOULD be held under the retired
    /// offer/decide model (default 12). Never read by the live engine.</summary>
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
