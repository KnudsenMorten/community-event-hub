namespace CommunityHub.Core.Domain;

/// <summary>
/// A POST-SESSION attendee EVALUATION of a <see cref="Session"/> (a HappyOrNot-style
/// quick rating), submitted from a PUBLIC, no-login page addressed by the session's
/// unguessable <see cref="Session.PublicToken"/> (<c>/sessions/{token}/evaluate</c>),
/// typically reached by scanning the room QR.
///
/// This is DISTINCT from <see cref="SessionQuestion"/>: a question is asked BEFORE the
/// event (topics an attendee wants covered) and is shown only to organizers/speakers; an
/// evaluation is a quick rating + optional comment gathered AFTER (or during) the session,
/// aggregated for the organizer results dashboard. They share the public-token seam but
/// are separate entities, tables and flows.
///
/// <b>Anti-abuse (lightweight, not heavy auth):</b> one rating per attendee per session is
/// enforced softly — the public page drops a per-session cookie marker and the service
/// upserts on the supplied <see cref="VoterKey"/> (the cookie value), so a repeat visit on
/// the same device updates the existing rating rather than stacking duplicates. A honeypot
/// + an IP-hash soft rate-limit (mirroring the survey / session-question form) sit in front
/// of it. None of this is a login: it is best-effort de-duplication, not identity.
///
/// Edition-scoped via <see cref="EventId"/>; the rating is never shown back publicly.
/// </summary>
public class SessionEvaluation
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- The session being evaluated ----------------------------------------
    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    /// <summary>
    /// The smiley / star rating on a 1–5 scale (1 = very unhappy … 5 = very happy).
    /// Required; validated in range by <see cref="SessionEvaluationService"/>.
    /// </summary>
    public int Rating { get; set; }

    /// <summary>Optional free-text comment. Blank = rating only. Capped.</summary>
    public string? Comment { get; set; }

    /// <summary>
    /// The lightweight one-per-attendee de-dup key: the value of the per-session
    /// cookie the public page sets (an unguessable random token). The service upserts
    /// on (SessionId, VoterKey) so a repeat submit from the same device updates the
    /// existing rating instead of adding a duplicate. NOT an identity / login; null
    /// when no cookie could be set (the row still counts).
    /// </summary>
    public string? VoterKey { get; set; }

    /// <summary>
    /// SHA-256 of the requester's IP (truncated). Soft rate-limit only; never PII'd
    /// back to the IP. Mirrors <see cref="SessionQuestion.IpHash"/>.
    /// </summary>
    public string? IpHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the rating was last updated (a same-device re-rate). Null = never.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
