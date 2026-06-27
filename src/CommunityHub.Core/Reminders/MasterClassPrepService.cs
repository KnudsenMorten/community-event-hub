using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Server-side authority for the MASTER CLASS attendee LANDING PAGE (FEATURE 2):
/// prep content, the public-within-the-MC Q&amp;A comment thread, and 1:1 private
/// questions. ALL access decisions live here so the permission model is enforced in
/// ONE place regardless of which page (speaker hub, attendee landing) calls it.
///
/// Access model:
///  - <b>Edit prep</b>: a speaker LINKED to the MC session (via
///    <see cref="SessionSpeaker"/>) OR an organizer. Stamps PrepUpdatedAt/By.
///  - <b>View / comment / ask</b>: a confirmed <see cref="MasterClassSignup"/> for
///    that session (the attendee path) OR the MC's speakers / an organizer (always).
///
/// Edition-scoped. Authorization failures throw
/// <see cref="MasterClassPrepAccessDeniedException"/> (pages map to 403); bad input
/// throws <see cref="MasterClassPrepValidationException"/>.
/// </summary>
public sealed class MasterClassPrepService
{
    /// <summary>Max length of a stored comment / 1:1 question (matches the columns).</summary>
    public const int MaxTextLength = 2000;

    /// <summary>Max length of the prep content (matches the column).</summary>
    public const int MaxPrepLength = 8000;

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public MasterClassPrepService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Header + body data for the master-class attendee landing page.</summary>
    /// <param name="SessionId">The master-class session id.</param>
    /// <param name="Title">The master-class title (page heading).</param>
    /// <param name="Room">The room, when known.</param>
    /// <param name="PrepContent">The published prep content (null/blank = nothing yet).</param>
    /// <param name="PrepUpdatedAt">When the prep content was last edited.</param>
    /// <param name="LogisticsText">The published "before you arrive / what to bring" logistics (null/blank = nothing yet).</param>
    /// <param name="LogisticsUpdatedAt">When the logistics text was last edited.</param>
    /// <param name="Speakers">The MC's speaker display names ("presented by").</param>
    public sealed record LandingView(
        int SessionId,
        string Title,
        string? Room,
        string? PrepContent,
        DateTimeOffset? PrepUpdatedAt,
        string? LogisticsText,
        DateTimeOffset? LogisticsUpdatedAt,
        IReadOnlyList<string> Speakers);

    /// <summary>
    /// Load the master-class landing-page header (title / room / prep / speakers), or
    /// null when the session is not a master class in the edition. Does NOT enforce
    /// access — the page applies the per-viewer gate (confirmed attendee OR the MC's
    /// speakers / an organizer) before showing this.
    /// </summary>
    public async Task<LandingView?> GetLandingAsync(
        int eventId, int sessionId, CancellationToken ct = default)
    {
        var s = await _db.Sessions
            .AsNoTracking()
            .Include(x => x.SessionSpeakers).ThenInclude(ss => ss.Participant)
            .FirstOrDefaultAsync(
                x => x.Id == sessionId && x.EventId == eventId
                     && x.Type == SessionType.MasterClass, ct);
        if (s is null) return null;

        var speakers = s.SessionSpeakers
            .Select(ss => ss.Participant?.FullName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LandingView(
            s.Id, s.Title, s.Room, s.PrepContent, s.PrepUpdatedAt,
            s.LogisticsText, s.LogisticsUpdatedAt, speakers);
    }

    // =====================================================================
    //  Capability checks.
    // =====================================================================

    /// <summary>True when the session exists, is a master class, in the edition.</summary>
    private Task<bool> IsMasterClassAsync(int eventId, int sessionId, CancellationToken ct) =>
        _db.Sessions.AnyAsync(
            s => s.Id == sessionId && s.EventId == eventId
                 && s.Type == SessionType.MasterClass, ct);

    /// <summary>True when the participant is a speaker linked to this MC session.</summary>
    private Task<bool> IsLinkedSpeakerAsync(int sessionId, int participantId, CancellationToken ct) =>
        _db.SessionSpeakers.AnyAsync(
            ss => ss.SessionId == sessionId && ss.ParticipantId == participantId, ct);

    /// <summary>
    /// May the participant EDIT this master class's prep content? TRUE for an
    /// organizer in the edition or a speaker linked to the session. False when the
    /// session is not a master class.
    /// </summary>
    public async Task<bool> CanEditAsync(
        int eventId, int sessionId, int participantId, ParticipantRole role,
        CancellationToken ct = default)
    {
        if (!await IsMasterClassAsync(eventId, sessionId, ct)) return false;
        if (role == ParticipantRole.Organizer) return true;
        return await IsLinkedSpeakerAsync(sessionId, participantId, ct);
    }

    /// <summary>
    /// May a HUB participant (speaker / organizer) VIEW this master class's landing
    /// page? Same gate as edit — the MC's speakers + organizers can always view.
    /// </summary>
    public Task<bool> CanParticipantViewAsync(
        int eventId, int sessionId, int participantId, ParticipantRole role,
        CancellationToken ct = default) =>
        CanEditAsync(eventId, sessionId, participantId, role, ct);

    /// <summary>
    /// May an ATTENDEE view / comment / ask on this master class? TRUE only when they
    /// hold a CONFIRMED <see cref="MasterClassSignup"/> for the session.
    /// </summary>
    public Task<bool> AttendeeHasConfirmedSeatAsync(
        int eventId, int sessionId, int attendeeId, CancellationToken ct = default) =>
        _db.MasterClassSignups.AnyAsync(
            x => x.EventId == eventId && x.SessionId == sessionId
                 && x.AttendeeId == attendeeId
                 && x.Status == MasterClassSignupStatus.Confirmed, ct);

    // =====================================================================
    //  Prep content (speaker / organizer edit).
    // =====================================================================

    /// <summary>
    /// Apply an edit to the master class's prep content, gated by
    /// <see cref="CanEditAsync"/>. Throws
    /// <see cref="MasterClassPrepAccessDeniedException"/> when the editor is neither a
    /// linked speaker nor an organizer. Stamps PrepUpdatedAt + PrepUpdatedByParticipantId.
    /// </summary>
    public async Task<Session> UpdatePrepAsync(
        int eventId, int sessionId, int participantId, ParticipantRole role,
        string? prepContent, CancellationToken ct = default)
    {
        if (!await CanEditAsync(eventId, sessionId, participantId, role, ct))
        {
            throw new MasterClassPrepAccessDeniedException(
                "Only a linked speaker or an organizer may edit this master class's prep.");
        }

        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct)
            ?? throw new MasterClassPrepValidationException(
                $"Session {sessionId} not found in event {eventId}.");

        var trimmed = string.IsNullOrWhiteSpace(prepContent) ? null : prepContent.Trim();
        if (trimmed is { Length: > MaxPrepLength }) trimmed = trimmed[..MaxPrepLength];

        var now = _clock.GetUtcNow();
        session.PrepContent = trimmed;
        session.PrepUpdatedAt = now;
        session.PrepUpdatedByParticipantId = participantId;
        session.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// The MASTER-CLASS sessions a speaker is linked to in the edition (§136 — the
    /// speaker Group Q&amp;A view loads one separate, per-session board for each). A
    /// speaker sees only the master classes they present; co-speakers on the same MC
    /// share the same board (it is keyed by <see cref="Session"/> id, not by speaker).
    /// Ordered by title for a stable, labelled layout. Includes the linked speakers so
    /// the page can show co-speakers without an extra round-trip.
    /// </summary>
    public async Task<List<Session>> LoadSpeakerMasterClassesAsync(
        int eventId, int participantId, CancellationToken ct = default) =>
        await _db.Sessions
            .AsNoTracking()
            .Include(s => s.SessionSpeakers).ThenInclude(ss => ss.Participant)
            .Where(s => s.EventId == eventId
                        && s.Type == SessionType.MasterClass
                        && s.SessionSpeakers.Any(ss => ss.ParticipantId == participantId))
            .OrderBy(s => s.Title)
            .ToListAsync(ct);

    // =====================================================================
    //  Q&A comments (public within the MC).
    // =====================================================================

    /// <summary>One master-class session's comment thread, oldest-first.</summary>
    public async Task<List<MasterClassComment>> LoadCommentsAsync(
        int eventId, int sessionId, CancellationToken ct = default) =>
        await _db.MasterClassComments
            .Where(c => c.EventId == eventId && c.SessionId == sessionId)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(ct);

    /// <summary>
    /// Post a comment as a HUB participant (a linked speaker or an organizer). Gated by
    /// <see cref="CanEditAsync"/> (speakers/organizers can always participate).
    /// </summary>
    public async Task<MasterClassComment> AddParticipantCommentAsync(
        int eventId, int sessionId, int participantId, ParticipantRole role,
        string body, int? parentCommentId = null, CancellationToken ct = default)
    {
        if (!await CanParticipantViewAsync(eventId, sessionId, participantId, role, ct))
        {
            throw new MasterClassPrepAccessDeniedException(
                "Only the master class's speakers, organizers or a confirmed attendee may comment.");
        }

        var name = await _db.Participants
            .Where(p => p.Id == participantId)
            .Select(p => p.FullName).FirstOrDefaultAsync(ct) ?? "Speaker";

        return await AddCommentCoreAsync(
            eventId, sessionId, name, body, parentCommentId,
            authorParticipantId: participantId, authorAttendeeId: null, ct);
    }

    /// <summary>
    /// Post a comment as a confirmed ATTENDEE. Gated by
    /// <see cref="AttendeeHasConfirmedSeatAsync"/>.
    /// </summary>
    public async Task<MasterClassComment> AddAttendeeCommentAsync(
        int eventId, int sessionId, int attendeeId,
        string body, int? parentCommentId = null, CancellationToken ct = default)
    {
        if (!await AttendeeHasConfirmedSeatAsync(eventId, sessionId, attendeeId, ct))
        {
            throw new MasterClassPrepAccessDeniedException(
                "A confirmed Master Class seat is required to comment.");
        }

        var att = await _db.Attendees
            .Where(a => a.Id == attendeeId)
            .Select(a => new { a.FirstName, a.LastName }).FirstOrDefaultAsync(ct);
        var name = $"{att?.FirstName} {att?.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "Attendee";

        return await AddCommentCoreAsync(
            eventId, sessionId, name, body, parentCommentId,
            authorParticipantId: null, authorAttendeeId: attendeeId, ct);
    }

    private async Task<MasterClassComment> AddCommentCoreAsync(
        int eventId, int sessionId, string authorName, string body, int? parentCommentId,
        int? authorParticipantId, int? authorAttendeeId, CancellationToken ct)
    {
        body = (body ?? string.Empty).Trim();
        if (body.Length < 1)
            throw new MasterClassPrepValidationException("Please enter a comment.");
        if (body.Length > MaxTextLength) body = body[..MaxTextLength];

        // A threaded reply must point at a comment on the SAME master class.
        if (parentCommentId is int pid)
        {
            var ok = await _db.MasterClassComments.AnyAsync(
                c => c.Id == pid && c.SessionId == sessionId && c.EventId == eventId, ct);
            if (!ok) parentCommentId = null; // ignore a bad/foreign parent rather than fail
        }

        var comment = new MasterClassComment
        {
            EventId = eventId,
            SessionId = sessionId,
            AuthorParticipantId = authorParticipantId,
            AuthorAttendeeId = authorAttendeeId,
            AuthorDisplayName = authorName.Length > 200 ? authorName[..200] : authorName,
            Body = body,
            ParentCommentId = parentCommentId,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.MasterClassComments.Add(comment);
        await _db.SaveChangesAsync(ct);
        return comment;
    }

    // =====================================================================
    //  1:1 private questions (reuse SessionQuestion).
    // =====================================================================

    /// <summary>
    /// A confirmed attendee submits a PRIVATE 1:1 question from the landing page. It is
    /// stored as a <see cref="SessionQuestion"/> with <see cref="SessionQuestion.IsPrivate"/>
    /// = true + the asker attendee id, so it surfaces to the MC's speakers (Speaker
    /// hub) and its answer returns to this attendee. Gated by a confirmed seat.
    /// </summary>
    public async Task<SessionQuestion> AskPrivateQuestionAsync(
        int eventId, int sessionId, int attendeeId, string questionText,
        CancellationToken ct = default)
    {
        if (!await AttendeeHasConfirmedSeatAsync(eventId, sessionId, attendeeId, ct))
        {
            throw new MasterClassPrepAccessDeniedException(
                "A confirmed Master Class seat is required to ask a 1:1 question.");
        }

        questionText = (questionText ?? string.Empty).Trim();
        if (questionText.Length < 2)
            throw new MasterClassPrepValidationException("Please enter your question.");
        if (questionText.Length > MaxTextLength) questionText = questionText[..MaxTextLength];

        var att = await _db.Attendees
            .Where(a => a.Id == attendeeId)
            .Select(a => new { a.FirstName, a.LastName, a.Email }).FirstOrDefaultAsync(ct);

        var q = new SessionQuestion
        {
            EventId = eventId,
            SessionId = sessionId,
            IsPrivate = true,
            AskerAttendeeId = attendeeId,
            AskerName = $"{att?.FirstName} {att?.LastName}".Trim(),
            AskerEmail = att?.Email,
            QuestionText = questionText,
            Status = SessionQuestionStatus.Open,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.SessionQuestions.Add(q);
        await _db.SaveChangesAsync(ct);
        return q;
    }

    /// <summary>
    /// The attendee's OWN 1:1 questions for this master class (with the speakers'
    /// responses, when answered) — for the landing page. Newest-first.
    /// </summary>
    public async Task<List<SessionQuestion>> LoadMyPrivateQuestionsAsync(
        int eventId, int sessionId, int attendeeId, CancellationToken ct = default) =>
        await _db.SessionQuestions
            .Where(q => q.EventId == eventId && q.SessionId == sessionId
                        && q.IsPrivate && q.AskerAttendeeId == attendeeId)
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync(ct);
}

/// <summary>Thrown when the actor lacks permission for a master-class prep/landing action.
/// Pages map this to a 403 (Forbid).</summary>
public sealed class MasterClassPrepAccessDeniedException : Exception
{
    public MasterClassPrepAccessDeniedException(string message) : base(message) { }
}

/// <summary>Thrown for bad input (empty comment/question, unknown session).
/// Pages map this to a friendly validation message.</summary>
public sealed class MasterClassPrepValidationException : Exception
{
    public MasterClassPrepValidationException(string message) : base(message) { }
}
