using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// One of the signed-in speaker's OWN sessions with its attendee-evaluation
/// roll-up, projected for the speaker self-service "My session ratings" page
/// (REQUIREMENTS §6 / §20 Speaker). Read-only.
///
/// The post-session attendee evaluations (a HappyOrNot-style 1–5 smiley rating +
/// optional anonymous comment, gathered via the room-QR public page) already feed
/// the ORGANIZER results dashboard and an organizer-triggered results email to the
/// speaker; this gives the speaker a first-class, always-on self-service view of
/// the same numbers for THEIR sessions only — never another speaker's.
/// </summary>
/// <param name="SessionId">The session id, for cross-linking to the public detail page (<c>/Sessions/{id}</c>).</param>
/// <param name="Title">The session title.</param>
/// <param name="Room">The assigned room, when published (else null).</param>
/// <param name="IsMasterClass">True for a community master class.</param>
/// <param name="Count">How many attendee evaluations this session has.</param>
/// <param name="AverageRating">The mean 1–5 rating, rounded to 2 dp; null when there are no ratings yet.</param>
/// <param name="Comments">The non-blank anonymous comments, newest first.</param>
public sealed record MySpeakerSessionRatings(
    int SessionId,
    string Title,
    string? Room,
    bool IsMasterClass,
    int Count,
    double? AverageRating,
    IReadOnlyList<MySpeakerEvaluationComment> Comments);

/// <summary>One anonymous attendee comment on a speaker's session (rating + text + when).</summary>
/// <param name="Rating">The 1–5 smiley rating the comment was left with.</param>
/// <param name="Comment">The free-text comment (never blank — blank comments are excluded).</param>
/// <param name="CreatedAt">When the evaluation was submitted.</param>
public sealed record MySpeakerEvaluationComment(
    int Rating,
    string Comment,
    DateTimeOffset CreatedAt);

/// <summary>The whole "My session ratings" payload for a speaker.</summary>
/// <param name="Sessions">Per-session rating roll-ups, most-rated first.</param>
/// <param name="TotalCount">Total evaluations across all the speaker's sessions.</param>
/// <param name="OverallAverage">The mean rating across all of them; null when none yet.</param>
public sealed record MySpeakerEvaluationsResult(
    IReadOnlyList<MySpeakerSessionRatings> Sessions,
    int TotalCount,
    double? OverallAverage);

/// <summary>
/// Builds the signed-in speaker's OWN attendee-evaluation roll-up for the speaker
/// "My session ratings" page. <b>Own-row scoped, server-enforced:</b> every query
/// is filtered to <c>EventId == eventId</c> AND the session is one the
/// <c>participantId</c> is actually a <see cref="SessionSpeaker"/> on — a speaker
/// can never see another speaker's evaluations through this service. Only speaker
/// roles get a non-empty result; any other role returns an empty result (the page
/// anyway gates non-speakers out, but the service is defensive).
///
/// Read-only: never writes. Service sessions (breaks/lunch) are excluded. The
/// comments are anonymous — the underlying <see cref="SessionEvaluation"/> never
/// stores attendee identity, so nothing here exposes who rated.
/// </summary>
public sealed class SpeakerEvaluationsService
{
    private readonly CommunityHubDbContext _db;

    public SpeakerEvaluationsService(CommunityHubDbContext db) => _db = db;

    private static readonly ParticipantRole[] SpeakerRoles =
    {
        ParticipantRole.Speaker,
    };

    /// <summary>
    /// The speaker's own (non-service) sessions in this edition, each with its
    /// attendee-evaluation count / average / comments. Sessions are ordered
    /// most-rated first, then by title. Returns an empty result for a non-speaker
    /// role or a speaker with no linked sessions.
    /// </summary>
    public async Task<MySpeakerEvaluationsResult> GetMyEvaluationsAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
    {
        if (!SpeakerRoles.Contains(role))
            return new MySpeakerEvaluationsResult(Array.Empty<MySpeakerSessionRatings>(), 0, null);

        // Own-row scope: only sessions this participant is a SessionSpeaker on, in
        // this edition, excluding service sessions. SQL-translatable.
        var sessions = await _db.Sessions
            .Where(s => s.EventId == eventId
                        && !s.IsServiceSession
                        && s.SessionSpeakers.Any(ss => ss.ParticipantId == participantId))
            .Select(s => new { s.Id, s.Title, s.Room, s.Type })
            .ToListAsync(ct);

        if (sessions.Count == 0)
            return new MySpeakerEvaluationsResult(Array.Empty<MySpeakerSessionRatings>(), 0, null);

        var sessionIds = sessions.Select(s => s.Id).ToList();

        // Pull the raw evaluations for just those sessions; aggregate in memory so
        // the per-session average + the comment list stay simple + SQL-translatable.
        var evals = await _db.SessionEvaluations
            .Where(x => x.EventId == eventId && sessionIds.Contains(x.SessionId))
            .Select(x => new { x.SessionId, x.Rating, x.Comment, x.CreatedAt })
            .ToListAsync(ct);

        var bySession = evals
            .GroupBy(x => x.SessionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var perSession = sessions
            .Select(s =>
            {
                bySession.TryGetValue(s.Id, out var rows);
                rows ??= new();
                var comments = rows
                    .Where(r => !string.IsNullOrWhiteSpace(r.Comment))
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => new MySpeakerEvaluationComment(r.Rating, r.Comment!, r.CreatedAt))
                    .ToList();
                return new MySpeakerSessionRatings(
                    s.Id,
                    s.Title,
                    string.IsNullOrWhiteSpace(s.Room) ? null : s.Room,
                    s.Type == SessionType.MasterClass,
                    rows.Count,
                    rows.Count == 0 ? null : Math.Round(rows.Average(r => r.Rating), 2),
                    comments);
            })
            // Most-rated first, then by title for a stable order.
            .OrderByDescending(a => a.Count)
            .ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalCount = evals.Count;
        var overallAvg = totalCount == 0
            ? (double?)null
            : Math.Round(evals.Average(e => e.Rating), 2);

        return new MySpeakerEvaluationsResult(perSession, totalCount, overallAvg);
    }
}
