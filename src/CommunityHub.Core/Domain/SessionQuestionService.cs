using System.Security.Cryptography;
using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Domain;

/// <summary>
/// Server-side authority for the ATTENDEE-QUESTION-PER-SESSION feature. ALL reads
/// and mutations go through here so the visibility / permission model is enforced
/// in ONE place, regardless of which page (public, organizer, speaker) calls it.
///
/// Flow + model:
///  - PUBLIC (no login): an attendee opens a session's public page by its
///    unguessable <see cref="Session.PublicToken"/> and submits a question. The
///    question lands in the hub ONLY (a <see cref="SessionQuestion"/> linked to the
///    session) and is NEVER auto-public. Spam handling (honeypot + IP-hash soft
///    rate-limit) lives in the page, mirroring the survey form; the IP hash is
///    passed through to <see cref="SubmitPublicQuestionAsync"/> for storage.
///  - ORGANIZER: sees ALL questions across the edition and may respond to any.
///  - SPEAKER: sees + may respond to questions ONLY for sessions they are linked to
///    (<see cref="SessionSpeaker"/>). A speaker's response is then visible to the
///    OTHER speakers on the same session (and organizers), so co-speakers
///    coordinate.
///
/// Authorization failures throw <see cref="SessionQuestionAccessDeniedException"/>
/// (pages map to 403); bad input throws
/// <see cref="SessionQuestionValidationException"/>. Everything is edition-scoped.
/// </summary>
public sealed class SessionQuestionService
{
    /// <summary>Max length of a stored question / response (matches the column).</summary>
    public const int MaxTextLength = 2000;

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SessionQuestionService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The signed-in actor, as the pages know them from the session.</summary>
    public readonly record struct ActorContext(
        int ParticipantId, string Email, ParticipantRole Role, int EventId);

    // =====================================================================
    //  Public token (unguessable) for a session's public ask page.
    // =====================================================================

    /// <summary>
    /// Return the session's public token, minting one on first use. Idempotent:
    /// once a token exists it is returned unchanged. The token (not the sequential
    /// id) is what addresses <c>/sessions/{token}/ask</c>, so the public URL can't
    /// be enumerated.
    /// </summary>
    public async Task<string> EnsurePublicTokenAsync(int sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new SessionQuestionValidationException($"Session {sessionId} not found.");

        if (!string.IsNullOrWhiteSpace(session.PublicToken))
            return session.PublicToken!;

        session.PublicToken = NewToken();
        await _db.SaveChangesAsync(ct);
        return session.PublicToken!;
    }

    /// <summary>
    /// Resolve a presented public token to its session (with speakers loaded for
    /// the public landing page), or null when the token is empty / unknown. The
    /// unique index makes this an exact lookup.
    /// </summary>
    public async Task<Session?> ResolveByPublicTokenAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var trimmed = token.Trim();
        return await _db.Sessions
            .AsNoTracking()
            .Include(s => s.Event)
            .Include(s => s.SessionSpeakers).ThenInclude(ss => ss.Participant)
            .FirstOrDefaultAsync(s => s.PublicToken == trimmed, ct);
    }

    // =====================================================================
    //  Public submit (NO auth) — lands in the hub only, never auto-public.
    // =====================================================================

    /// <summary>
    /// Store a public attendee question for the session addressed by
    /// <paramref name="publicToken"/>. Name/email are optional; the question text
    /// is required and capped. Returns null when the token does not resolve (the
    /// page maps that to 404). The honeypot/rate-limit decision is the caller's;
    /// when the caller decides to persist it passes the IP hash here for storage.
    /// </summary>
    public async Task<SessionQuestion?> SubmitPublicQuestionAsync(
        string publicToken, string? askerName, string? askerEmail,
        string questionText, string? ipHash, CancellationToken ct = default)
    {
        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.PublicToken == (publicToken ?? string.Empty).Trim(), ct);
        if (session is null) return null;

        questionText = (questionText ?? string.Empty).Trim();
        if (questionText.Length < 2)
            throw new SessionQuestionValidationException("Please enter your question.");
        if (questionText.Length > MaxTextLength)
            questionText = questionText[..MaxTextLength];

        var q = new SessionQuestion
        {
            EventId = session.EventId,
            SessionId = session.Id,
            AskerName = Clean(askerName, 200),
            AskerEmail = Clean(askerEmail, 320),
            QuestionText = questionText,
            IpHash = string.IsNullOrWhiteSpace(ipHash) ? null : ipHash,
            Status = SessionQuestionStatus.Open,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.SessionQuestions.Add(q);
        await _db.SaveChangesAsync(ct);
        return q;
    }

    /// <summary>
    /// Count how many questions an IP hash has submitted to an edition since
    /// <paramref name="since"/>. The public page uses this for a soft rate-limit
    /// (never PII'd back to the IP). Returns 0 when the hash is blank.
    /// </summary>
    public async Task<int> CountRecentByIpHashAsync(
        int eventId, string? ipHash, DateTimeOffset since, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ipHash)) return 0;
        return await _db.SessionQuestions.CountAsync(
            q => q.EventId == eventId && q.IpHash == ipHash && q.CreatedAt >= since, ct);
    }

    // =====================================================================
    //  Capability checks.
    // =====================================================================

    /// <summary>
    /// True if the actor may see / respond to questions for
    /// <paramref name="sessionId"/>: an ORGANIZER anywhere in the edition, or a
    /// SPEAKER linked to exactly that session.
    /// </summary>
    public async Task<bool> CanAccessSessionAsync(
        ActorContext actor, int sessionId, CancellationToken ct = default)
    {
        if (actor.Role == ParticipantRole.Organizer)
            return await _db.Sessions.AnyAsync(
                s => s.Id == sessionId && s.EventId == actor.EventId, ct);

        if (actor.Role is ParticipantRole.Speaker)
            return await _db.SessionSpeakers.AnyAsync(
                ss => ss.SessionId == sessionId
                      && ss.ParticipantId == actor.ParticipantId
                      && ss.Session.EventId == actor.EventId, ct);

        return false;
    }

    private async Task RequireAccessAsync(ActorContext actor, int sessionId, CancellationToken ct)
    {
        if (!await CanAccessSessionAsync(actor, sessionId, ct))
            throw new SessionQuestionAccessDeniedException(
                $"Participant {actor.ParticipantId} may not access questions for session {sessionId}.");
    }

    // =====================================================================
    //  Reads (scope-enforced).
    // =====================================================================

    /// <summary>
    /// ORGANIZER view: ALL questions across the edition, newest-first within
    /// status, with the session + asker response author loaded. Organizer-only.
    /// </summary>
    public async Task<List<SessionQuestion>> LoadAllForEventAsync(
        ActorContext actor, CancellationToken ct = default)
    {
        if (actor.Role != ParticipantRole.Organizer)
            throw new SessionQuestionAccessDeniedException("Organizer role required.");

        return await _db.SessionQuestions
            .Where(q => q.EventId == actor.EventId)
            .Include(q => q.Session)
            .Include(q => q.RespondedByParticipant)
            .OrderBy(q => q.Status)
            .ThenByDescending(q => q.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Questions for ONE session, scope-enforced (organizer anywhere, or a speaker
    /// linked to the session). This is the SAME set every speaker on the session
    /// sees, so a co-speaker's response is visible to the others.
    /// </summary>
    public async Task<List<SessionQuestion>> LoadForSessionAsync(
        ActorContext actor, int sessionId, CancellationToken ct = default)
    {
        await RequireAccessAsync(actor, sessionId, ct);
        return await _db.SessionQuestions
            .Where(q => q.SessionId == sessionId && q.EventId == actor.EventId)
            .Include(q => q.RespondedByParticipant)
            .OrderBy(q => q.Status)
            .ThenByDescending(q => q.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The sessions a speaker is linked to in an edition (for the speaker's
    /// "questions for my sessions" view). Organizers get an empty list here — they
    /// use <see cref="LoadAllForEventAsync"/>.
    /// </summary>
    public async Task<List<Session>> LoadMySessionsAsync(
        ActorContext actor, CancellationToken ct = default)
    {
        if (actor.Role is not ParticipantRole.Speaker)
            return new List<Session>();

        return await _db.Sessions
            .Where(s => s.EventId == actor.EventId
                        && s.SessionSpeakers.Any(ss => ss.ParticipantId == actor.ParticipantId))
            .Include(s => s.SessionSpeakers).ThenInclude(ss => ss.Participant)
            .OrderBy(s => s.StartsAt == null)
            .ThenBy(s => s.StartsAt)
            .ThenBy(s => s.Title)
            .ToListAsync(ct);
    }

    // =====================================================================
    //  Respond (involved speaker or organizer).
    // =====================================================================

    /// <summary>
    /// An involved speaker (or an organizer) responds to a question. Scope is the
    /// session: a speaker may only answer questions on a session they are linked
    /// to. Stamps the responder + time and moves the status to
    /// <see cref="SessionQuestionStatus.Answered"/> (or
    /// <see cref="SessionQuestionStatus.Closed"/> if requested). The response is
    /// then visible to the OTHER speakers on the same session + organizers.
    /// </summary>
    public async Task<bool> RespondAsync(
        ActorContext actor, int questionId, string responseText,
        SessionQuestionStatus newStatus = SessionQuestionStatus.Answered,
        CancellationToken ct = default)
    {
        var q = await _db.SessionQuestions.FirstOrDefaultAsync(
            x => x.Id == questionId && x.EventId == actor.EventId, ct);
        if (q is null) return false;
        await RequireAccessAsync(actor, q.SessionId, ct);

        if (newStatus == SessionQuestionStatus.Open)
            throw new SessionQuestionValidationException(
                "A response must move the question to Answered or Closed.");

        responseText = (responseText ?? string.Empty).Trim();
        if (responseText.Length < 1)
            throw new SessionQuestionValidationException("Please enter a response.");
        if (responseText.Length > MaxTextLength)
            responseText = responseText[..MaxTextLength];

        q.ResponseText = responseText;
        q.RespondedByParticipantId = actor.ParticipantId;
        q.RespondedByEmail = actor.Email;
        q.RespondedAt = _clock.GetUtcNow();
        q.Status = newStatus;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Close a question without a textual response (organizer/involved speaker).</summary>
    public async Task<bool> CloseAsync(ActorContext actor, int questionId, CancellationToken ct = default)
    {
        var q = await _db.SessionQuestions.FirstOrDefaultAsync(
            x => x.Id == questionId && x.EventId == actor.EventId, ct);
        if (q is null) return false;
        await RequireAccessAsync(actor, q.SessionId, ct);

        q.Status = SessionQuestionStatus.Closed;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // =====================================================================
    //  Helpers.
    // =====================================================================

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        return t.Length > max ? t[..max] : t;
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32); // 256 bits
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}

/// <summary>Thrown when the actor lacks permission for a session-question action.
/// Pages map this to a 403 (Forbid).</summary>
public sealed class SessionQuestionAccessDeniedException : Exception
{
    public SessionQuestionAccessDeniedException(string message) : base(message) { }
}

/// <summary>Thrown for bad input (empty question/response, unknown session).
/// Pages map this to a friendly validation message.</summary>
public sealed class SessionQuestionValidationException : Exception
{
    public SessionQuestionValidationException(string message) : base(message) { }
}
