namespace CommunityHub.Core.Domain;

/// <summary>
/// §326bs — how many rooms the organizers have CONTRACTED at one hotel for ONE
/// night. A hotel contract does not hold a flat block: it holds a different
/// number per night (the ELDK27 AC Bella Sky contract runs 3, 6, 26, 60, 100,
/// 40, 3, 2 across 5–12 Feb 2027), so a single
/// <see cref="Hotel.RoomBlockSize"/> cannot express it.
///
/// <para>One row per (<see cref="EventId"/>, <see cref="HotelId"/>,
/// <see cref="Night"/>). <see cref="Night"/> is the night SLEPT, i.e. the night
/// of 5 Feb is the stay 5 Feb → 6 Feb — the same convention
/// <see cref="HotelBooking.CheckInDate"/> / <see cref="HotelBooking.CheckOutDate"/>
/// use, so a booking covers every night in <c>[CheckInDate, CheckOutDate)</c>.</para>
///
/// <para>This is the CONTRACTED supply. Demand is computed live from the
/// participants placed in the hotel; the two are compared per night so an
/// organizer can see where to ask for more rooms and where to release rooms
/// before a <see cref="HotelCutoff"/> makes them non-refundable.</para>
///
/// <para>Distinct from <see cref="Hotel.RoomBlockSize"/>, which stays as the
/// coarse single-number block for editions that do not need per-night detail.</para>
/// </summary>
public class HotelAllotment
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int HotelId { get; set; }
    public Hotel Hotel { get; set; } = null!;

    /// <summary>The night slept (5 Feb = the 5→6 Feb stay). Date only, no time.</summary>
    public DateOnly Night { get; set; }

    /// <summary>
    /// Contracted rooms for this night. Non-negative; 0 is legitimate (a night the
    /// contract covers with no rooms held) and is NOT the same as having no row at
    /// all, which means "nothing contracted / not part of the deal".
    /// </summary>
    public int RoomsAllotted { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// §326bs — a contractual RELEASE DEADLINE on a hotel's allotment: the date after
/// which unreleased rooms start costing money. The ELDK27 contract has three
/// (free until 7 Dec 2026; then 20%/day to 6 Jan 2027; then a further 10%/day to
/// 26 Jan 2027; after that 100% charge), and each is a date the organizers must
/// act BEFORE, not on.
///
/// <para>The hub does not enforce or compute the contract's arithmetic — the terms
/// differ per hotel and per year. It records the dates and what each allows, shows
/// them against the live over/under picture, and REMINDS the organizers 3 days
/// ahead so a release window is never missed by accident.</para>
/// </summary>
public class HotelCutoff
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int HotelId { get; set; }
    public Hotel Hotel { get; set; } = null!;

    /// <summary>The date the window closes. Rooms must be released ON or BEFORE it.</summary>
    public DateOnly CutoffDate { get; set; }

    /// <summary>
    /// What this deadline is, in the contract's own words — e.g. "Free cancellation
    /// deadline" or "Period 1: max 20% of the original per day". Shown on the page
    /// and in the reminder mail.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// How much of the ORIGINAL reservation may still be released free of charge from
    /// this date (e.g. 20). Null when the contract states no percentage — the label
    /// then carries the whole rule. Purely informational: never used to auto-release.
    /// </summary>
    public int? ReleasePercent { get; set; }

    /// <summary>Free-text detail for the organizers (clause reference, who to email, …).</summary>
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
