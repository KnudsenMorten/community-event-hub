namespace CommunityHub.Core.Domain.Evaluation;

/// <summary>
/// §743 C3 — which speakers a mirrored session belongs to. The brief's <c>SessionSpeaker</c>:
/// many rows per session, populated by the CEH sync, never entered by hand.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This is NOT an authorisation record, and must never become one.</b> §743.3 embedded
/// the speaker view in CEH — speakers never log in to the evaluation system — so who may READ a
/// session's results is decided by CEH's own <c>MineAsSpeaker(eventId, participantId)</c> predicate,
/// against data CEH is the master of. These rows exist for exactly three things that have nothing to
/// do with login: the report's <b>speaker-history</b> section (C7), the <b>report-ready
/// notification</b> to a session's speakers (C0), and <b>scope=speaker</b> aggregates (§6).</para>
///
/// <para>🔑 <b>Why a second table when CEH already has <c>SessionSpeaker</c>.</b> CEH's link is
/// <c>SessionId → ParticipantId</c> and is the better key — but it is a key into CEH's participant
/// table, which the evaluation system is meant to be liftable away from. Keeping a denormalised
/// e-mail here is what lets the whole evaluation model be extracted as a routing change rather than
/// a rewrite. The duplication is the deliverable, not an oversight.</para>
///
/// <para>🔒 <b><see cref="SpeakerEmail"/> is <c>Participant.Email</c> — the PRIMARY address — and
/// nothing else</b> (§743.2, operator: <i>"use primary email as login"</i>).
/// <c>ContactEmailOverride</c>, <c>SecondaryEmail</c>, <c>CalendarEmail</c> and
/// <c>AlternateEmail</c> are delivery or alternate addresses; none of them is the identity CEH signs
/// a session in with. "The speaker's email" is genuinely ambiguous in this schema, and picking the
/// wrong column would look right until somebody sets one — PROD currently has 0 contact overrides,
/// so that mistake would pass every test we can run today.</para>
///
/// <para>🔒 <b>Stored normalised</b> (<see cref="Auth.PinLoginService.NormalizeEmail"/> —
/// <c>Trim().ToLowerInvariant()</c>), because that is what a signed-in session's claim is compared
/// against. A mixed-case export would otherwise fail to match for exactly those people whose address
/// was typed with capitals.</para>
///
/// <para>⚠️ <b>No <c>EventId</c> column, deliberately.</b> The event is reached through
/// <see cref="EvaluationSession"/>, which already carries it. A second FK to <c>Events</c> beside the
/// cascade from the session would give SQL Server two cascade paths to the same root — the §744.1
/// trap that failed a deploy, and one EF InMemory cannot see.</para>
/// </remarks>
public class EvaluationSessionSpeaker
{
    public int Id { get; set; }

    public int EvaluationSessionId { get; set; }
    public EvaluationSession EvaluationSession { get; set; } = null!;

    /// <summary>
    /// The speaker's PRIMARY e-mail, normalised. The grouping and mailing key — never a credential.
    /// </summary>
    public string SpeakerEmail { get; set; } = string.Empty;

    /// <summary>
    /// Display only, for the report and the notification. 🔑 Never a match key: the brief is explicit
    /// that a display name is not sufficient to identify a person.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The CEH participant this came from, recorded for diagnostics only. 🔒 Deliberately NOT a
    /// foreign key and never joined on — the moment anything resolves a speaker through it, the
    /// extraction story this table exists to protect is gone.
    /// </summary>
    public int? CehParticipantId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
