namespace CommunityHub.Core.Domain;

/// <summary>The lifecycle of a <see cref="SessionQuestion"/>.</summary>
public enum SessionQuestionStatus
{
    /// <summary>Asked by a (public) attendee, not yet answered.</summary>
    Open = 0,
    /// <summary>An involved speaker (or an organizer) has responded.</summary>
    Answered = 1,
    /// <summary>The question is closed (handled / no longer relevant).</summary>
    Closed = 2,
}

/// <summary>
/// An ATTENDEE QUESTION asked for a <see cref="Session"/> BEFORE the event, via a
/// public, no-login page (<c>/sessions/{token}/ask</c>). The question lands in the
/// Event Hub ONLY — it is NEVER auto-public and is never shown back on the public
/// page; only organizers and the session's own speakers ever read it.
///
/// Use case: masterclass logistics / topics attendees want covered, gathered ahead
/// of time so speakers can prepare.
///
/// Visibility model (enforced server-side in <see cref="SessionQuestionService"/>):
///  - ORGANIZERS see ALL questions across the edition.
///  - An INVOLVED SPEAKER (a speaker linked to the session via
///    <see cref="SessionSpeaker"/>) sees the questions for THEIR session(s) and may
///    respond. A speaker's response is then visible to the OTHER speakers on the
///    same session (and organizers), so co-speakers can coordinate.
///  - The public never reads questions or responses.
///
/// Asker name/email are OPTIONAL (an attendee can ask anonymously); only the
/// free-text <see cref="QuestionText"/> is required. Edition-scoped via
/// <see cref="EventId"/>; spam handling (honeypot + IP-hash soft rate-limit) lives
/// in the public page, mirroring the survey form.
/// </summary>
public class SessionQuestion
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // --- The session the question is about ----------------------------------
    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    // --- Who asked (both optional — anonymous asks are allowed) --------------
    /// <summary>Optional attendee name. Blank = anonymous.</summary>
    public string? AskerName { get; set; }

    /// <summary>
    /// Optional attendee email (e.g. so a speaker could follow up). NOT a login
    /// identity and NOT verified; blank = none. Never shown publicly.
    /// </summary>
    public string? AskerEmail { get; set; }

    /// <summary>The attendee's free-text question. Required; capped at 2000 chars.</summary>
    public string QuestionText { get; set; } = string.Empty;

    // --- 1:1 private question (master-class landing page, FEATURE 2) ---------

    /// <summary>
    /// True when this is a PRIVATE 1:1 question submitted by a confirmed attendee
    /// from the master-class landing page (FEATURE 2), routed to the MC's speakers.
    /// The speakers' response is shown back to that attendee on the landing page;
    /// it is never shown to other attendees. False for the pre-event public ask
    /// (the original use, visible to organizers + the session's speakers only).
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>
    /// The confirmed <see cref="Domain.Attendee"/> who asked the 1:1 private
    /// question, so the landing page can show that attendee their own questions +
    /// the speakers' responses. Null for a public (anonymous) pre-event ask.
    /// </summary>
    public int? AskerAttendeeId { get; set; }
    public Attendee? AskerAttendee { get; set; }

    /// <summary>
    /// SHA-256 of the requester's IP (truncated). Soft rate-limit only; never
    /// PII'd back to the IP. Mirrors <see cref="SurveyResponse.IpHash"/>.
    /// </summary>
    public string? IpHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // --- Response (set when an involved speaker / organizer answers) --------
    public SessionQuestionStatus Status { get; set; } = SessionQuestionStatus.Open;

    /// <summary>The speaker's / organizer's reply text (null until answered).</summary>
    public string? ResponseText { get; set; }

    /// <summary>The participant (speaker or organizer) who responded.</summary>
    public int? RespondedByParticipantId { get; set; }
    public Participant? RespondedByParticipant { get; set; }

    /// <summary>Email of whoever responded, for audit.</summary>
    public string? RespondedByEmail { get; set; }

    public DateTimeOffset? RespondedAt { get; set; }
}
