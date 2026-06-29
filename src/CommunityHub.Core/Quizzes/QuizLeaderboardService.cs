using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Quizzes;

/// <summary>
/// EF-backed adapter over the pure <see cref="QuizLeaderboard"/> reducer
/// (REQUIREMENTS §171): loads an edition's COMPLETED attempts for a quiz with the
/// players' privacy-safe names and produces the top-N + the viewer's own rank. All
/// ranking/tie-break/keep-best logic lives in the pure reducer; this only fetches.
/// </summary>
public sealed class QuizLeaderboardService
{
    private readonly CommunityHubDbContext _db;

    public QuizLeaderboardService(CommunityHubDbContext db) => _db = db;

    /// <summary>The default leaderboard size (top 10).</summary>
    public const int DefaultTopN = 10;

    /// <summary>
    /// The per-quiz leaderboard: the top <paramref name="topN"/> by best score
    /// (tie-break fastest), plus <paramref name="viewerParticipantId"/>'s own row
    /// even when outside the top N. Only completed attempts count.
    /// </summary>
    public async Task<QuizLeaderboardView> GetAsync(
        int eventId, int quizId, int viewerParticipantId,
        int topN = DefaultTopN, CancellationToken ct = default)
    {
        var rows = await (
            from a in _db.QuizAttempts
            where a.EventId == eventId && a.QuizId == quizId && a.CompletedAt != null
            join p in _db.Participants on a.ParticipantId equals p.Id
            select new { a.ParticipantId, p.FullName, a.Score, a.ElapsedMs })
            .ToListAsync(ct);

        var results = rows.Select(r => new QuizAttemptResult(
            r.ParticipantId, QuizLeaderboard.DisplayName(r.FullName), r.Score, r.ElapsedMs));

        return QuizLeaderboard.Build(results, viewerParticipantId, topN);
    }
}
