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

    /// <summary>
    /// §1386 — the hotel's CANCELLATION number, filled in by the hotel when we cancel a room and
    /// imported from their returned spreadsheet. Kept so the next spreadsheet still carries it.
    /// </summary>
    public string? CancellationNumber { get; set; }

    /// <summary>§1386 — when the reservation became Cancelled.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>
    /// §1386 — the hotel the cancelled room was at. A drop-out is un-placed
    /// (<see cref="Participant.HotelId"/> → null), and without this the cancellation would vanish
    /// from every hotel's spreadsheet instead of reaching the hotel that holds the room.
    /// </summary>
    public int? CancelledHotelId { get; set; }

    /// <summary>
    /// §1386 — how many people sleep in this room, as an organizer set it (operator 2026-11-08: <i>"i must also be
    /// able to adjust how many people goes in a room"</i>). Null = not set: 2 when a share-with name is given, else 1
    /// (<see cref="EffectiveGuestsInRoom"/>). Printed as "Number of Guests in room" on the hotel spreadsheet.
    /// </summary>
    public int? GuestsInRoom { get; set; }

    /// <summary>
    /// §1386 — the PARTICIPANT this guest shares a room with (two volunteers in one room). Both then count as ONE room
    /// in every per-night number and on the hotel sheet (see <see cref="Organizer.HotelRoomMath"/>). A partner who is
    /// not a participant is not a roommate: use <see cref="GuestsInRoom"/> = 2 instead.
    /// </summary>
    public int? RoommateParticipantId { get; set; }

    /// <summary>§1386 — the organizer's number, else the default.</summary>
    public static int EffectiveGuestsInRoom(int? set, string? shareWith) =>
        set is int n && n > 0 ? n : (string.IsNullOrWhiteSpace(shareWith) ? 1 : 2);
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
    /// <summary>
    /// §1386 — the room was given up (the participant no longer needs it, or an organizer cancelled
    /// it). The row stays on the hotel's spreadsheet as Cancelled so the hotel learns about it, and
    /// carries the hotel's cancellation number once it comes back. Never counts as demand.
    /// </summary>
    Cancelled = 2,
}

/// <summary>§1386 — the reservation state as the hotel spreadsheet and the pages name it.</summary>
public static class HotelReservationStateText
{
    public const string Reserved = "Reserved";
    public const string Confirmed = "Confirmed";
    public const string Cancelled = "Cancelled";

    public static string Of(HotelConfirmationState s) => s switch
    {
        HotelConfirmationState.Confirmed => Confirmed,
        HotelConfirmationState.Cancelled => Cancelled,
        _ => Reserved,
    };

    /// <summary>Parse the hotel's own wording back; null when the cell says nothing we recognise.</summary>
    public static HotelConfirmationState? Parse(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.StartsWith("confirm", StringComparison.OrdinalIgnoreCase)) return HotelConfirmationState.Confirmed;
        if (t.StartsWith("cancel", StringComparison.OrdinalIgnoreCase)) return HotelConfirmationState.Cancelled;
        if (t.StartsWith("reserv", StringComparison.OrdinalIgnoreCase)) return HotelConfirmationState.NotConfirmed;
        return null;
    }
}
