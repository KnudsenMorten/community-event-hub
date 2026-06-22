namespace CommunityHub.Core.Domain;

/// <summary>
/// One company's group-photo session at the event (README: Group photos
/// management). The organizer registers the company + lead contact, picks a
/// time slot, and sends a calendar invite to the lead plus any internal
/// participants. Re-sending uses a stable ICS UID, so an updated slot
/// UPDATES the existing calendar entry instead of duplicating it.
/// </summary>
public class GroupPhotoRegistration
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public string CompanyName { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>
    /// Number of tickets in the company's volume package. The group photo is a
    /// perk for the larger packages: only companies with MORE THAN
    /// <see cref="QualifyingTicketThreshold"/> tickets qualify. Entered manually
    /// by the organizer (there is no automated ticket-volume feed); 0 = unset.
    /// </summary>
    public int TicketCount { get; set; }

    /// <summary>A company qualifies for the group photo above this ticket count.</summary>
    public const int QualifyingTicketThreshold = 10;

    /// <summary>True when the company's volume package qualifies it for the group photo.</summary>
    public bool Qualifies => TicketCount > QualifyingTicketThreshold;

    /// <summary>
    /// Comma/semicolon-separated internal staff emails — kept for the organizer's
    /// reference only. The calendar invite goes to the appointed company lead
    /// (<see cref="ContactEmail"/>) ONLY; these are NOT invited (operator 2026-06-22).
    /// </summary>
    public string InternalParticipants { get; set; } = string.Empty;

    /// <summary>Photo slot start (UTC). Null = registered but not scheduled yet.</summary>
    public DateTimeOffset? ScheduledAtUtc { get; set; }

    /// <summary>Slot length; photo sessions are short.</summary>
    public int DurationMinutes { get; set; } = 15;

    public string? Location { get; set; }
    public string? Notes { get; set; }

    public DateTimeOffset? InviteLastSentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
