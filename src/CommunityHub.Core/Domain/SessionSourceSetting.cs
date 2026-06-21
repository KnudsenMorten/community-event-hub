namespace CommunityHub.Core.Domain;

/// <summary>
/// Per-edition choice of which SESSION source is active (Sessionize vs Zoho
/// Backstage) — set by an organizer in Settings, so the switch is config/UI-driven
/// rather than a deploy (REQUIREMENTS §6). One row per edition (unique on
/// <see cref="EventId"/>); no row ⇒ the shipped default (Sessionize). Speakers are
/// always sourced from Sessionize; this governs only sessions.
/// </summary>
public class SessionSourceSetting
{
    public int Id { get; set; }

    /// <summary>Edition scope (unique).</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The active source key (<c>SessionSourceKinds</c>: sessionize | backstage).</summary>
    public string Source { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedByEmail { get; set; }
}
