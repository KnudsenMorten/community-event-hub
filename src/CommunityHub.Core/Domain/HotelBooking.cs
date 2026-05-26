namespace CommunityHub.Core.Domain;

/// <summary>
/// A participant's hotel booking request for an edition (CONTEXT.md section 9
/// forms). One per participant per event. The official hotel and coverage
/// rules come from hotel.&lt;edition&gt;.json; this row is the participant's input.
/// </summary>
public class HotelBooking
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>Whether the participant needs a hotel room at all.</summary>
    public bool NeedsRoom { get; set; }

    public DateOnly? CheckInDate { get; set; }
    public DateOnly? CheckOutDate { get; set; }

    /// <summary>Optional name of someone to share a room with.</summary>
    public string? RoomShareWith { get; set; }

    /// <summary>Free-text notes for the organizers.</summary>
    public string? Notes { get; set; }

    // --- Vendor confirmation workflow ---------------------------------------
    public HotelConfirmationState ConfirmationState { get; set; } = HotelConfirmationState.NotConfirmed;
    /// <summary>Confirmation number from AC Bella Sky once the import lands.</summary>
    public string? ConfirmationNumber { get; set; }
    /// <summary>Room type / category (vendor-supplied).</summary>
    public string? RoomType { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? CalendarInviteSentAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Where the hotel booking is in the vendor workflow.</summary>
public enum HotelConfirmationState
{
    /// <summary>Participant submitted, vendor confirmation pending.</summary>
    NotConfirmed = 0,
    /// <summary>Vendor returned a confirmation number; calendar invite re-issued as CONFIRMED.</summary>
    Confirmed = 1,
}
