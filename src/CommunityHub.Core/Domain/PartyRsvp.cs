namespace CommunityHub.Core.Domain;

/// <summary>
/// An anonymous (no-login) RSVP to the edition's Party. Captured from a public
/// form — name + email + whether the person opts in to attend (the Party runs
/// 16:00–18:00 on the pre-day). Upserted by (EventId, Email) so a re-submit
/// updates the same row rather than duplicating. Organizers get a headcount + an
/// export; nothing here grants hub access (it is not a Participant).
/// </summary>
public class PartyRsvp
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>The opt-in: true = will attend the Party.</summary>
    public bool Attending { get; set; }

    /// <summary>Hash of the submitter IP (soft anti-abuse; never reversible to PII).</summary>
    public string? IpHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
