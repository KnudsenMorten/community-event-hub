namespace CommunityHub.Core.Domain;

/// <summary>
/// One row in the UNIFIED AUDIT TRAIL (REQUIREMENTS §24): an append-only record of a
/// single action in the system — a user action (any mutating request), a backend /
/// engine action (an email sent, a sync run), or an auth event. The organizer reviews
/// these in <c>/Organizer/AuditTrail</c> for troubleshooting ("why did X happen?") and
/// usage insight ("how many calendar syncs?"). Never mutated after insert.
///
/// PII stance: we store the actor's identity + a human summary + a stable action code,
/// NOT raw request payloads (no PINs, no email bodies). Rich detail goes in
/// <see cref="Detail"/> only when the write point supplies a safe summary.
/// </summary>
public class AuditEntry
{
    public long Id { get; set; }

    /// <summary>The edition this action belongs to (0 when none can be resolved,
    /// e.g. an anonymous auth attempt before sign-in).</summary>
    public int EventId { get; set; }

    /// <summary>When the action occurred (UTC).</summary>
    public DateTimeOffset OccurredUtc { get; set; }

    /// <summary>Broad bucket for filtering + usage counts.</summary>
    public AuditCategory Category { get; set; }

    /// <summary>Stable machine action code (see <see cref="Audit.AuditActions"/>),
    /// e.g. <c>calendar.subscribe</c>, <c>email.sent</c>, <c>hotel.assign</c>, or for
    /// auto-captured user actions the <c>METHOD path[/handler]</c> shape.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>The acting participant's id, when known (null for anonymous / system).</summary>
    public int? ActorParticipantId { get; set; }

    /// <summary>The acting participant's email / label (or "anonymous" / "system").</summary>
    public string ActorEmail { get; set; } = string.Empty;

    /// <summary>The acting participant's role at action time (e.g. Organizer, Speaker).</summary>
    public string? ActorRole { get; set; }

    /// <summary>True when performed inside an impersonation ("acting as") session — in
    /// which case <see cref="ActorEmail"/> is the REAL actor and
    /// <see cref="OnBehalfOf"/> is whom they were acting as.</summary>
    public bool IsActingAs { get; set; }

    /// <summary>Whom the actor was acting as (impersonation target label), if any.</summary>
    public string? OnBehalfOf { get; set; }

    /// <summary>Optional subject of the action (e.g. "Participant", "Hotel", "Session").</summary>
    public string? TargetType { get; set; }

    /// <summary>Optional subject id (e.g. the participant/hotel/session id).</summary>
    public string? TargetId { get; set; }

    /// <summary>One-line human-readable summary for the trail UI.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Optional extra (safe) detail — never raw secrets/payloads.</summary>
    public string? Detail { get; set; }

    /// <summary>Whether the action succeeded, failed, or was denied.</summary>
    public AuditOutcome Outcome { get; set; } = AuditOutcome.Success;

    /// <summary>Where the action originated.</summary>
    public AuditSource Source { get; set; } = AuditSource.Web;

    /// <summary>HTTP method for auto-captured web actions (null for engine/job events).</summary>
    public string? HttpMethod { get; set; }

    /// <summary>Request path for auto-captured web actions (no query string).</summary>
    public string? Path { get; set; }
}

/// <summary>Broad audit bucket (filter + usage counts).</summary>
public enum AuditCategory
{
    /// <summary>A user-initiated mutating request (the auto-captured default).</summary>
    UserAction = 0,
    /// <summary>Sign-in / sign-out / magic-link / impersonation.</summary>
    Auth = 1,
    /// <summary>An outbound email (engine).</summary>
    Email = 2,
    /// <summary>Calendar subscribe / .ics sync (engine + user).</summary>
    CalendarSync = 3,
    /// <summary>A backend job / sync / integration run (engine).</summary>
    Engine = 4,
    /// <summary>An organizer admin/config change.</summary>
    Admin = 5,
}

/// <summary>The outcome of an audited action.</summary>
public enum AuditOutcome
{
    Success = 0,
    Failure = 1,
    Denied = 2,
}

/// <summary>Where an audited action originated.</summary>
public enum AuditSource
{
    Web = 0,
    Job = 1,
    System = 2,
}
