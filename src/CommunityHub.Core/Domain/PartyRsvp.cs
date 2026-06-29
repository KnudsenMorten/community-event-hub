namespace CommunityHub.Core.Domain;

/// <summary>
/// An RSVP to the edition's Party (the Party runs 16:00–18:30 on the pre-day).
/// Two flavours share this one row (§164):
///   • ANONYMOUS (no-login) — captured from the public form: name + email + the
///     opt-in. <see cref="ParticipantId"/> is null; nothing here grants hub access.
///   • AUTHENTICATED — a signed-in participant RSVPs as themselves: name + email
///     come from their hub profile and <see cref="ParticipantId"/> is stamped so the
///     reminder/task wiring can mark their "party sign-up" task Done.
/// Upserted by (EventId, Email) so a re-submit updates the same row rather than
/// duplicating. Organizers get a headcount + an export.
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

    /// <summary>
    /// §164: SPONSOR head count — "how many will attend from your company?". Set
    /// only when a sponsor RSVPs (their company brings several people); null for
    /// every non-sponsor and for anonymous RSVPs (a single person = themselves).
    /// </summary>
    public int? HeadCount { get; set; }

    /// <summary>
    /// §164: the signed-in participant who submitted this RSVP, or null for an
    /// anonymous public submission. Nullable FK (no cascade) — an RSVP never
    /// requires a participant, and deleting a participant must not drop the
    /// edition's headcount row.
    /// </summary>
    public int? ParticipantId { get; set; }
    public Participant? Participant { get; set; }

    /// <summary>Hash of the submitter IP (soft anti-abuse; never reversible to PII).</summary>
    public string? IpHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
