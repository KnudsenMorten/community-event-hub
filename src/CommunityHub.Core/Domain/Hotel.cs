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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    // --- Navigation ---------------------------------------------------------
    /// <summary>Participants placed in this hotel (one hotel → many people).</summary>
    public ICollection<Participant> Participants { get; set; } = new List<Participant>();
}
