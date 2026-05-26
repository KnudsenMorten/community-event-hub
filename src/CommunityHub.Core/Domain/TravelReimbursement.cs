namespace CommunityHub.Core.Domain;

/// <summary>
/// A speaker's travel reimbursement request. Only relevant to speakers
/// travelling from outside Denmark.
///
/// Policy (ELDK27):
///  - Economy flights only.
///  - Single-speaker session: up to EUR 400 per speaker.
///  - Co-speaking session: up to EUR 300 per person.
///  - If the speaker cannot find flights within the cap, they tick
///    "CannotStayInLimits" and the organizer team handles it manually.
/// </summary>
public class TravelReimbursement
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>True = the speaker is requesting reimbursement.</summary>
    public bool RequestReimbursement { get; set; }

    /// <summary>Origin city / country (e.g. "Berlin, Germany").</summary>
    public string? OriginCity { get; set; }

    /// <summary>
    /// Amount the speaker is claiming, in EUR. Self-asserted from the
    /// available caps (300 single-speaker-session per person, 400 single
    /// or any) or a custom value with an Explanation when neither fits.
    /// The system does NOT enforce a cap -- the organizer reviews and pays
    /// out per claim.
    /// </summary>
    public decimal? ClaimAmountEur { get; set; }

    /// <summary>Free-text explanation (required when ClaimAmountEur doesn't match a standard cap).</summary>
    public string? Explanation { get; set; }

    // --- Organizer-only fields (not editable by the speaker) ----------------
    /// <summary>Organizer marks paid when the reimbursement has been transferred.</summary>
    public bool IsPaid { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? PaidNotes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
