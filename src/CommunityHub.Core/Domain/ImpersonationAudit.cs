namespace CommunityHub.Core.Domain;

/// <summary>
/// One audit record of an <b>acting-as</b> event: an organizer (or a secretary
/// via a secure token) entering, acting in, or leaving another participant's
/// context. Impersonation is a sensitive capability, so every boundary
/// transition and on-behalf write is recorded here — who acted, as whom, by
/// which mechanism, and what they did.
///
/// This is append-only history (never updated or deleted by the app); the
/// organizer can review it. Edition-scoped like everything else.
/// </summary>
public class ImpersonationAudit
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>How the acting session was established.</summary>
    public ImpersonationActorKind ActorKind { get; set; }

    /// <summary>
    /// The acting participant id when the actor is an organizer (null for a
    /// secretary-token session, whose actor is an external person, not a
    /// participant row).
    /// </summary>
    public int? ActorParticipantId { get; set; }

    /// <summary>Human-readable actor (organizer name/email, or "secretary token").</summary>
    public string ActorLabel { get; set; } = string.Empty;

    /// <summary>The participant whose context was entered / acted upon.</summary>
    public int TargetParticipantId { get; set; }

    /// <summary>
    /// What happened: <c>"start"</c>, <c>"return"</c>, or an on-behalf action
    /// code such as <c>"modify-hotel"</c> / <c>"modify-swag"</c> /
    /// <c>"complete-task"</c>.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Free-text detail (e.g. the field changed and its new value).</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>How an acting-as session was established.</summary>
public enum ImpersonationActorKind
{
    /// <summary>An organizer used "Switch to user" on the grid.</summary>
    Organizer = 0,

    /// <summary>An external secretary used a secure-token URL.</summary>
    SecretaryToken = 1,
}
