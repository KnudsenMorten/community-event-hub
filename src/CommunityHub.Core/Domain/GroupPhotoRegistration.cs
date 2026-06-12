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

    /// <summary>Comma/semicolon-separated internal staff emails copied on the invite.</summary>
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
