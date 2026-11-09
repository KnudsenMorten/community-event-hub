namespace CommunityHub.Core.Domain;

/// <summary>
/// An organizer-defined hotel for an edition. Rooms for an event rarely fit in a
/// single hotel, so attendees are split across several. Organizers CRUD these
/// rows, then assign each <see cref="Participant"/> to one
/// (<see cref="Participant.HotelId"/>) and manage room blocks per hotel.
///
/// Distinct from <see cref="HotelBooking"/>, which is the participant's own
/// preference input (needs-a-room / dates / share-with). A <see cref="Hotel"/>
/// is the physical venue an organizer places people into; one hotel holds many
/// participants. Scoped to one <see cref="Event"/> via <see cref="EventId"/> so a
/// new edition is just new rows — no schema change.
/// </summary>
public class Hotel
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Hotel display name (e.g. "Central Plaza Hotel"). Required.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Postal / street address shown to assigned participants + in their email.</summary>
    public string? Address { get; set; }

    /// <summary>Hotel reception / booking contact email, for organizers.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>Free-text notes for the organizers (room block ref, rate code, …).</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// The size of the room block reserved at this hotel — how many rooms the
    /// organizers have held. Optional (null = no block size recorded yet, so the
    /// occupancy view shows "not set" rather than a misleading 0). When set, the
    /// room-block occupancy view compares it against the number of people placed
    /// here so organizers can see at a glance whether a hotel is under, at, or
    /// over its block. A non-negative count (0 is allowed — e.g. a hotel held for
    /// reference with no rooms reserved).
    /// </summary>
    public int? RoomBlockSize { get; set; }

    /// <summary>
    /// The booking CONFIRMATION NUMBER the hotel releases for this room block
    /// (REQUIREMENTS §46). An organizer enters it once, after the hotel confirms.
    /// While null/blank the reservation is "not confirmed"; once a non-blank value
    /// is recorded the reservation is treated as <c>[CONFIRMED]</c> and every
    /// participant placed in this hotel who needs a room is re-sent their hotel
    /// calendar invite carrying this number. Participant-facing surfaces show it
    /// read-only; only organizers set it.
    /// </summary>
    public string? ConfirmationNumber { get; set; }

    /// <summary>
    /// True once a non-blank <see cref="ConfirmationNumber"/> has been recorded —
    /// the reservation is CONFIRMED. Computed, not stored.
    /// </summary>
    public bool IsConfirmed => !string.IsNullOrWhiteSpace(ConfirmationNumber);

    /// <summary>
    /// §1386 — which hotel to fill first: 1 = the main agreement, 2/3 = secondary / buffer hotels used
    /// only when the main one cannot give more rooms. New reservations are placed in the
    /// lowest-numbered hotel automatically; the overview orders by it.
    /// </summary>
    public int Priority { get; set; } = 1;

    /// <summary>
    /// §1386 — the hotel's own agreement reference (e.g. a group agreement number). Printed in the
    /// title of the hotel spreadsheet. Not a per-guest confirmation number.
    /// </summary>
    public string? AgreementNumber { get; set; }

    /// <summary>§1386 — the label after the agreement number in the spreadsheet title (e.g. the edition name).</summary>
    public string? AgreementLabel { get; set; }

    /// <summary>
    /// §1386 — an external booking reference that is not a group agreement (e.g. a buffer
    /// reservation made through a booking site). Shown to organizers only.
    /// </summary>
    public string? BookingReference { get; set; }

    /// <summary>
    /// §1386 — who receives this hotel's change mails and deadline warnings (comma/semicolon
    /// separated). Internal organizer addresses only — CEH never mails the hotel itself; an organizer
    /// forwards the attached spreadsheet. Blank ⇒ the logistics review mailbox.
    /// </summary>
    public string? NotifyEmails { get; set; }

    /// <summary>§1386 — when an organizer last marked the hotel's spreadsheet as sent to the hotel, and who.</summary>
    public DateTimeOffset? SentToHotelAt { get; set; }
    public string? SentToHotelByEmail { get; set; }

    /// <summary>§1386 — the spreadsheet title, e.g. "Group Agreement number: 1627652 (ELDK 2027)".</summary>
    public string SheetTitle =>
        string.IsNullOrWhiteSpace(AgreementNumber)
            ? Name
            : $"Group Agreement number: {AgreementNumber.Trim()}"
              + (string.IsNullOrWhiteSpace(AgreementLabel) ? "" : $" ({AgreementLabel.Trim()})");

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    // --- Navigation ---------------------------------------------------------
    /// <summary>Participants placed in this hotel (one hotel → many people).</summary>
    public ICollection<Participant> Participants { get; set; } = new List<Participant>();
}
