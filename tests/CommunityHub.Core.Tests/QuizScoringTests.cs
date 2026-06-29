using CommunityHub.Core.Quizzes;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §171 scoring-curve contract (correctness + SPEED). Locks the documented
/// curve: wrong = 0; a correct answer earns basePoints × (1 − elapsed/budget), so
/// faster correct &gt; slower correct &gt; wrong, full points at t≈0, and 0 at/after
/// the per-question time budget (beat the clock).
/// </summary>
public sealed class QuizScoringTests
{
    private const int Seconds = 20;     // 20 000 ms budget
    private const int Base = 1000;

    [Fact]
    public void Wrong_answer_scores_zero_regardless_of_speed()
    {
        Assert.Equal(0, QuizScoring.Score(correct: false, elapsedMs: 0, Seconds, Base));
        Assert.Equal(0, QuizScoring.Score(correct: false, elapsedMs: 1, Seconds, Base));
    }

    [Fact]
    public void Instant_correct_earns_the_full_base()
    {
        Assert.Equal(Base, QuizScoring.Score(correct: true, elapsedMs: 0, Seconds, Base));
    }

    [Fact]
    public void Correct_at_or_after_the_budget_earns_zero()
    {
        Assert.Equal(0, QuizScoring.Score(correct: true, elapsedMs: 20_000, Seconds, Base));
        Assert.Equal(0, QuizScoring.Score(correct: true, elapsedMs: 25_000, Seconds, Base)); // clamped
    }

    [Fact]
    public void Halfway_correct_earns_about_half()
    {
        Assert.Equal(500, QuizScoring.Score(correct: true, elapsedMs: 10_000, Seconds, Base));
    }

    [Fact]
    public void Faster_correct_beats_slower_correct_beats_wrong()
    {
        var fast = QuizScoring.Score(correct: true, elapsedMs: 2_000, Seconds, Base);
        var slow = QuizScoring.Score(correct: true, elapsedMs: 16_000, Seconds, Base);
        var wrong = QuizScoring.Score(correct: false, elapsedMs: 2_000, Seconds, Base);

        Assert.True(fast > slow, $"fast {fast} should beat slow {slow}");
        Assert.True(slow > wrong, $"slow {slow} should beat wrong {wrong}");
        Assert.Equal(0, wrong);
    }
}
