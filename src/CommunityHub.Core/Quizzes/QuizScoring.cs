namespace CommunityHub.Core.Quizzes;

/// <summary>
/// The §171 scoring curve — correctness + SPEED. Pure + documented so the engine,
/// the tests and any future tuning read ONE definition.
///
/// <para><b>Curve:</b> a WRONG answer earns <c>0</c>. A CORRECT answer earns
/// <c>basePoints × (1 − fraction)</c>, where
/// <c>fraction = clamp(elapsedMs / (perQuestionSeconds × 1000), 0, 1)</c> — i.e. a
/// linear time-decay across the per-question budget. So a correct answer at t≈0 earns
/// the full <c>basePoints</c>, a correct answer halfway through the budget earns half,
/// and a correct answer at/after the budget (ran out the clock) earns <c>0</c> — the
/// "beat the clock" mechanic. Faster correct &gt; slower correct &gt; wrong = 0.</para>
///
/// <para>The attempt score is the SUM of the per-question awards; ties on the
/// leaderboard break on total elapsed time (faster wins).</para>
/// </summary>
public static class QuizScoring
{
    /// <summary>
    /// Points for a single answer. <paramref name="correct"/> false ⇒ 0. Otherwise the
    /// linear speed-decay of <paramref name="basePoints"/> across the
    /// <paramref name="perQuestionSeconds"/> budget given <paramref name="elapsedMs"/>.
    /// </summary>
    public static int Score(bool correct, long elapsedMs, int perQuestionSeconds, int basePoints)
    {
        if (!correct || basePoints <= 0) return 0;
        var budgetMs = Math.Max(1L, (long)perQuestionSeconds * 1000L);
        var clamped = Math.Clamp(elapsedMs, 0L, budgetMs);
        var fraction = (double)clamped / budgetMs;
        var points = basePoints * (1.0 - fraction);
        return (int)Math.Round(points, MidpointRounding.AwayFromZero);
    }
}
