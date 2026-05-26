namespace CommunityHub.Core.Domain;

/// <summary>
/// One pending action surfaced to the organizer team when a participant
/// changes data that affects a downstream vendor or process. Written by the
/// form-save handlers, drained by an organizer clicking "Mark resolved".
/// Idempotent on (EventId, Type, ParticipantId) -- repeat edits don't pile up.
/// </summary>
public class OrganizerActionItem
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>e.g. "hotel-changed", "dinner-changed", "swag-changed", "travel-changed", "lunch-changed".</summary>
    public string Type { get; set; } = string.Empty;

    public int? ParticipantId { get; set; }
    public Participant? Participant { get; set; }

    /// <summary>Free-text describing what changed (used by the action-queue UI).</summary>
    public string Summary { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedNotes { get; set; }
}
