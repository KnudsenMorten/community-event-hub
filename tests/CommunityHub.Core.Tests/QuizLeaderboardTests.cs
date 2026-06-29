using System.Linq;
using CommunityHub.Core.Quizzes;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §171 leaderboard reducer (pure). Locks: keep-best per participant, ranking by
/// score desc then elapsed asc (faster wins ties), top-N truncation, the viewer's own
/// row returned even when outside the top N, and the privacy-safe display name.
/// </summary>
public sealed class QuizLeaderboardTests
{
    private static QuizAttemptResult R(int pid, int score, long ms) => new(pid, $"P{pid}", score, ms);

    [Fact]
    public void Ranks_by_score_desc_then_elapsed_asc()
    {
        var view = QuizLeaderboard.Build(new[]
        {
            R(1, 500, 9000),
            R(2, 800, 12000),
            R(3, 800, 8000),   // same score as #2 but faster -> ranks above it
        }, viewerParticipantId: 1, topN: 10);

        Assert.Equal(new[] { 3, 2, 1 }, view.Top.Select(e => e.ParticipantId));
        Assert.Equal(new[] { 1, 2, 3 }, view.Top.Select(e => e.Rank));
    }

    [Fact]
    public void Keeps_each_participants_best_attempt()
    {
        var view = QuizLeaderboard.Build(new[]
        {
            R(1, 300, 5000),
            R(1, 900, 7000),    // best score for P1
            R(1, 900, 4000),    // same score, faster -> this is the kept one
            R(2, 600, 6000),
        }, viewerParticipantId: 1, topN: 10);

        Assert.Equal(2, view.TotalPlayers);                 // one row per participant
        var p1 = view.Top.Single(e => e.ParticipantId == 1);
        Assert.Equal(900, p1.Score);
        Assert.Equal(4000, p1.ElapsedMs);                   // the fastest of the tied best
        Assert.Equal(1, p1.Rank);
    }

    [Fact]
    public void Own_row_is_returned_even_when_outside_the_top_N()
    {
        var attempts = Enumerable.Range(1, 12)
            .Select(i => R(i, 1000 - i * 10, 5000))   // P1 best … P12 worst
            .ToList();

        var view = QuizLeaderboard.Build(attempts, viewerParticipantId: 12, topN: 3);

        Assert.Equal(3, view.Top.Count);                    // truncated to top 3
        Assert.DoesNotContain(view.Top, e => e.ParticipantId == 12);
        Assert.NotNull(view.Own);
        Assert.Equal(12, view.Own!.ParticipantId);
        Assert.Equal(12, view.Own!.Rank);                   // honest absolute rank
        Assert.True(view.Own!.IsViewer);
    }

    [Fact]
    public void Own_is_null_when_the_viewer_has_no_attempt()
    {
        var view = QuizLeaderboard.Build(new[] { R(1, 500, 5000) }, viewerParticipantId: 99, topN: 10);
        Assert.Null(view.Own);
    }

    [Theory]
    [InlineData("Alice Smith", "Alice S.")]
    [InlineData("Bob", "Bob")]
    [InlineData("  Carol  Danvers  ", "Carol D.")]
    [InlineData("", "Player")]
    [InlineData(null, "Player")]
    public void DisplayName_is_first_name_plus_last_initial(string? full, string expected)
    {
        Assert.Equal(expected, QuizLeaderboard.DisplayName(full));
    }
}
