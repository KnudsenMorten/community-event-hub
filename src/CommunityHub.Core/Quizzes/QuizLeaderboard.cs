namespace CommunityHub.Core.Quizzes;

/// <summary>A raw completed-attempt row fed to the leaderboard reducer.</summary>
/// <param name="ParticipantId">The player.</param>
/// <param name="DisplayName">Privacy-safe display name (first name + last initial).</param>
/// <param name="Score">Attempt score.</param>
/// <param name="ElapsedMs">Attempt total answered time (the tie-break).</param>
public readonly record struct QuizAttemptResult(
    int ParticipantId, string DisplayName, int Score, long ElapsedMs);

/// <summary>One ranked leaderboard row.</summary>
public sealed record QuizLeaderboardEntry(
    int Rank, int ParticipantId, string DisplayName, int Score, long ElapsedMs, bool IsViewer);

/// <summary>
/// The rendered leaderboard: the <see cref="Top"/> N rows, the viewer's OWN row
/// (even when outside the top N; null when they have no completed attempt), and the
/// total ranked players.
/// </summary>
public sealed record QuizLeaderboardView(
    IReadOnlyList<QuizLeaderboardEntry> Top,
    QuizLeaderboardEntry? Own,
    int TotalPlayers);

/// <summary>
/// The §171 leaderboard reducer — pure, so the ordering, the tie-break, the
/// keep-best reduction and the own-rank-when-outside-top-N rule are all unit-tested
/// independently of EF.
///
/// <para><b>One ranked attempt per participant:</b> a player keeps their BEST
/// attempt — highest score, ties broken by the FASTEST total time. <b>Ranking:</b>
/// score descending, then total elapsed ascending (faster wins ties), then
/// participant id for a stable order. The viewer's own row is always returned even
/// when they fall outside the top N, so they can see their standing.</para>
/// </summary>
public static class QuizLeaderboard
{
    /// <summary>
    /// Build the leaderboard view from raw completed attempts. Reduces to each
    /// participant's best attempt, ranks them, and returns the top
    /// <paramref name="topN"/> plus <paramref name="viewerParticipantId"/>'s own row.
    /// </summary>
    public static QuizLeaderboardView Build(
        IEnumerable<QuizAttemptResult> attempts, int viewerParticipantId, int topN)
    {
        // Keep best per participant: max score, then fastest (min elapsed).
        var best = attempts
            .GroupBy(a => a.ParticipantId)
            .Select(g => g
                .OrderByDescending(a => a.Score)
                .ThenBy(a => a.ElapsedMs)
                .First())
            .ToList();

        // Rank: score desc, elapsed asc, then id for a deterministic tie-break.
        var ranked = best
            .OrderByDescending(a => a.Score)
            .ThenBy(a => a.ElapsedMs)
            .ThenBy(a => a.ParticipantId)
            .Select((a, i) => new QuizLeaderboardEntry(
                Rank: i + 1,
                ParticipantId: a.ParticipantId,
                DisplayName: a.DisplayName,
                Score: a.Score,
                ElapsedMs: a.ElapsedMs,
                IsViewer: a.ParticipantId == viewerParticipantId))
            .ToList();

        var top = topN > 0 ? ranked.Take(topN).ToList() : ranked;
        var own = ranked.FirstOrDefault(e => e.ParticipantId == viewerParticipantId);

        return new QuizLeaderboardView(top, own, ranked.Count);
    }

    /// <summary>
    /// Privacy-safe player name (REQUIREMENTS §171): first name + last-name initial
    /// (e.g. "Alice B."). Never the email. Falls back to "Player" when no name.
    /// </summary>
    public static string DisplayName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "Player";
        var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return parts[0];
        var last = parts[^1];
        var initial = char.ToUpperInvariant(last[0]);
        return $"{parts[0]} {initial}.";
    }
}
