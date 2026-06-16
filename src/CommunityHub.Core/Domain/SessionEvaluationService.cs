using System.Security.Cryptography;
using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Domain;

/// <summary>
/// Server-side authority for the POST-SESSION attendee-EVALUATION feature (a
/// HappyOrNot-style quick rating). ALL reads and mutations go through here so the
/// rules live in ONE place regardless of which page (public submit, organizer
/// dashboard) calls it.
///
/// Flow + model:
///  - PUBLIC (no login): an attendee opens a session's evaluate page by the session's
///    unguessable <see cref="Session.PublicToken"/> (reached via the room QR) and
///    submits a 1–5 rating + optional comment. The rating lands in the hub ONLY (a
///    <see cref="SessionEvaluation"/> linked to the session) and is never shown back
///    publicly.
///  - ANTI-ABUSE (lightweight): one rating per attendee per session is enforced softly
///    by upserting on a per-session cookie token (<see cref="SessionEvaluation.VoterKey"/>)
///    — a same-device re-rate UPDATES the existing row. Honeypot + IP-hash soft
///    rate-limit live in the page, mirroring the survey / session-question form.
///  - ORGANIZER: the results dashboard reads per-session and per-room aggregates
///    (average score, count, comments), optionally filtered.
///
/// The public-token mint/resolve mirrors <see cref="SessionQuestionService"/> and shares
/// the SAME <see cref="Session.PublicToken"/> (one token addresses both the ask and the
/// evaluate page) — it is never re-minted if one already exists.
///
/// Bad input throws <see cref="SessionEvaluationValidationException"/> (pages map to a
/// friendly message). Everything is edition-scoped.
/// </summary>
public sealed class SessionEvaluationService
{
    /// <summary>Min/max of the smiley rating scale (1 = very unhappy … 5 = very happy).</summary>
    public const int MinRating = 1;
    public const int MaxRating = 5;

    /// <summary>Max length of a stored comment (matches the column).</summary>
    public const int MaxCommentLength = 2000;

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SessionEvaluationService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // =====================================================================
    //  Public token (shared with the ask page) for a session's public pages.
    // =====================================================================

    /// <summary>
    /// Return the session's public token, minting one on first use. Idempotent and
    /// SHARED with the ask page (<see cref="Session.PublicToken"/>): one token
    /// addresses both <c>/sessions/{token}/ask</c> and <c>/sessions/{token}/evaluate</c>,
    /// so the room QR can encode the evaluate URL without a second secret.
    /// </summary>
    public async Task<string> EnsurePublicTokenAsync(int sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new SessionEvaluationValidationException($"Session {sessionId} not found.");

        if (!string.IsNullOrWhiteSpace(session.PublicToken))
            return session.PublicToken!;

        session.PublicToken = NewToken();
        await _db.SaveChangesAsync(ct);
        return session.PublicToken!;
    }

    /// <summary>
    /// Resolve a presented public token to its session (with speakers loaded for the
    /// public evaluate page), or null when the token is empty / unknown.
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
    //  Public submit (NO auth) — one rating per attendee/session (soft).
    // =====================================================================

    /// <summary>
    /// Store (or update) a public attendee evaluation for the session addressed by
    /// <paramref name="publicToken"/>. The rating is required and must be in
    /// [<see cref="MinRating"/>, <see cref="MaxRating"/>]; the comment is optional and
    /// capped. When <paramref name="voterKey"/> (the per-session cookie token) matches an
    /// existing rating for the session it is UPDATED in place (one-per-attendee/session),
    /// otherwise a new row is added. Returns null when the token does not resolve (the
    /// page maps that to 404). The honeypot/rate-limit decision is the caller's; when the
    /// caller decides to persist it passes the IP hash here for storage.
    /// </summary>
    public async Task<SessionEvaluation?> SubmitPublicEvaluationAsync(
        string publicToken, int rating, string? comment,
        string? voterKey, string? ipHash, CancellationToken ct = default)
    {
        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.PublicToken == (publicToken ?? string.Empty).Trim(), ct);
        if (session is null) return null;

        if (rating < MinRating || rating > MaxRating)
            throw new SessionEvaluationValidationException(
                $"Please pick a rating from {MinRating} to {MaxRating}.");

        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (comment is { Length: > MaxCommentLength })
            comment = comment[..MaxCommentLength];

        var key = string.IsNullOrWhiteSpace(voterKey) ? null : voterKey.Trim();
        var now = _clock.GetUtcNow();

        // One-per-attendee/session: a same-device re-rate updates in place.
        SessionEvaluation? existing = null;
        if (key is not null)
        {
            existing = await _db.SessionEvaluations.FirstOrDefaultAsync(
                x => x.SessionId == session.Id && x.VoterKey == key, ct);
        }

        if (existing is not null)
        {
            existing.Rating = rating;
            existing.Comment = comment;
            existing.IpHash = string.IsNullOrWhiteSpace(ipHash) ? existing.IpHash : ipHash;
            existing.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        var eval = new SessionEvaluation
        {
            EventId = session.EventId,
            SessionId = session.Id,
            Rating = rating,
            Comment = comment,
            VoterKey = key,
            IpHash = string.IsNullOrWhiteSpace(ipHash) ? null : ipHash,
            CreatedAt = now,
        };
        _db.SessionEvaluations.Add(eval);
        await _db.SaveChangesAsync(ct);
        return eval;
    }

    /// <summary>
    /// Count how many evaluations an IP hash has submitted to an edition since
    /// <paramref name="since"/>. The public page uses this for a soft rate-limit (never
    /// PII'd back to the IP). Returns 0 when the hash is blank.
    /// </summary>
    public async Task<int> CountRecentByIpHashAsync(
        int eventId, string? ipHash, DateTimeOffset since, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ipHash)) return 0;
        return await _db.SessionEvaluations.CountAsync(
            x => x.EventId == eventId && x.IpHash == ipHash && x.CreatedAt >= since, ct);
    }

    // =====================================================================
    //  Organizer results dashboard — per-session + per-room aggregates.
    // =====================================================================

    /// <summary>Per-session aggregate row for the organizer dashboard.</summary>
    public sealed record SessionAggregate(
        int SessionId, string Title, SessionType Type, string? Room,
        int Count, double? AverageRating, IReadOnlyList<EvaluationComment> Comments);

    /// <summary>Per-room aggregate row (all sessions sharing the room rolled up).</summary>
    public sealed record RoomAggregate(
        string Room, int SessionCount, int Count, double? AverageRating);

    /// <summary>One comment in a session's evaluation list (rating + text + when).</summary>
    public sealed record EvaluationComment(int Rating, string Comment, DateTimeOffset CreatedAt);

    /// <summary>
    /// The full organizer results dashboard for an edition: per-session aggregates
    /// (count, average, comments) and per-room roll-ups, optionally filtered by session
    /// <paramref name="type"/> and/or <paramref name="room"/>. Read-only; never writes.
    /// </summary>
    public async Task<DashboardResult> BuildDashboardAsync(
        int eventId, SessionType? type = null, string? room = null,
        CancellationToken ct = default)
    {
        var roomFilter = string.IsNullOrWhiteSpace(room) ? null : room.Trim();

        var sessionsQ = _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession);
        if (type is not null) sessionsQ = sessionsQ.Where(s => s.Type == type);
        if (roomFilter is not null) sessionsQ = sessionsQ.Where(s => s.Room == roomFilter);

        var sessions = await sessionsQ
            .Select(s => new { s.Id, s.Title, s.Type, s.Room })
            .ToListAsync(ct);

        var sessionIds = sessions.Select(s => s.Id).ToList();

        var evals = await _db.SessionEvaluations
            .Where(x => x.EventId == eventId && sessionIds.Contains(x.SessionId))
            .Select(x => new { x.SessionId, x.Rating, x.Comment, x.CreatedAt })
            .ToListAsync(ct);

        var bySession = evals.GroupBy(x => x.SessionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var sessionAggs = sessions
            .Select(s =>
            {
                bySession.TryGetValue(s.Id, out var rows);
                rows ??= new();
                var comments = rows
                    .Where(r => !string.IsNullOrWhiteSpace(r.Comment))
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => new EvaluationComment(r.Rating, r.Comment!, r.CreatedAt))
                    .ToList();
                return new SessionAggregate(
                    s.Id, s.Title, s.Type, s.Room,
                    rows.Count,
                    rows.Count == 0 ? null : Math.Round(rows.Average(r => r.Rating), 2),
                    comments);
            })
            // Most-rated first, then by title for a stable order.
            .OrderByDescending(a => a.Count).ThenBy(a => a.Title)
            .ToList();

        var roomAggs = sessions
            .Where(s => !string.IsNullOrWhiteSpace(s.Room))
            .GroupBy(s => s.Room!)
            .Select(g =>
            {
                var ids = g.Select(s => s.Id).ToHashSet();
                var roomEvals = evals.Where(e => ids.Contains(e.SessionId)).ToList();
                return new RoomAggregate(
                    g.Key,
                    g.Count(),
                    roomEvals.Count,
                    roomEvals.Count == 0 ? null : Math.Round(roomEvals.Average(e => e.Rating), 2));
            })
            .OrderByDescending(r => r.Count).ThenBy(r => r.Room)
            .ToList();

        var totalCount = evals.Count;
        var overallAvg = totalCount == 0 ? (double?)null : Math.Round(evals.Average(e => e.Rating), 2);

        return new DashboardResult(sessionAggs, roomAggs, totalCount, overallAvg);
    }

    /// <summary>The complete dashboard payload (session + room aggregates + totals).</summary>
    public sealed record DashboardResult(
        IReadOnlyList<SessionAggregate> Sessions,
        IReadOnlyList<RoomAggregate> Rooms,
        int TotalCount,
        double? OverallAverage);

    /// <summary>The distinct non-blank room names in an edition (for the filter dropdown).</summary>
    public async Task<List<string>> ListRoomsAsync(int eventId, CancellationToken ct = default)
        => await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession && s.Room != null && s.Room != "")
            .Select(s => s.Room!)
            .Distinct()
            .OrderBy(r => r)
            .ToListAsync(ct);

    // =====================================================================
    //  Helpers.
    // =====================================================================

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32); // 256 bits
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}

/// <summary>Thrown for bad evaluation input (out-of-range rating, unknown session).
/// Pages map this to a friendly validation message.</summary>
public sealed class SessionEvaluationValidationException : Exception
{
    public SessionEvaluationValidationException(string message) : base(message) { }
}
