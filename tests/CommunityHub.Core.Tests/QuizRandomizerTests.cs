using System.Linq;
using CommunityHub.Core.Quizzes;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §171 anti-copy randomiser. Locks: deterministic (re-derivable for server-side
/// answer mapping), a valid permutation, an N-distinct subset draw, and that BOTH the
/// drawn question set AND the option order vary by seed (so neighbours can't copy).
/// </summary>
public sealed class QuizRandomizerTests
{
    [Fact]
    public void Permutation_is_deterministic_for_the_same_seed_and_salt()
    {
        var a = QuizRandomizer.Permutation(seed: 12345, salt: 7, count: 6);
        var b = QuizRandomizer.Permutation(seed: 12345, salt: 7, count: 6);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Permutation_is_a_valid_permutation()
    {
        var p = QuizRandomizer.Permutation(seed: 99, salt: 3, count: 8);
        Assert.Equal(Enumerable.Range(0, 8).OrderBy(x => x), p.OrderBy(x => x));
    }

    [Fact]
    public void DrawQuestionIds_returns_N_distinct_ids_from_the_pool()
    {
        var pool = Enumerable.Range(100, 12).ToList(); // 12 ids
        var drawn = QuizRandomizer.DrawQuestionIds(pool, seed: 42, n: 8);

        Assert.Equal(8, drawn.Count);
        Assert.Equal(8, drawn.Distinct().Count());          // no repeats
        Assert.All(drawn, id => Assert.Contains(id, pool)); // subset of the pool
    }

    [Fact]
    public void DrawQuestionIds_returns_all_when_pool_is_smaller_than_N()
    {
        var pool = new[] { 1, 2, 3 };
        var drawn = QuizRandomizer.DrawQuestionIds(pool, seed: 1, n: 8);
        Assert.Equal(3, drawn.Count);
        Assert.Equal(new[] { 1, 2, 3 }, drawn.OrderBy(x => x));
    }

    [Fact]
    public void Drawn_question_set_varies_by_seed()
    {
        var pool = Enumerable.Range(1, 12).ToList();
        // Across several seeds the drawn sets are not all identical (anti-copy).
        var sets = Enumerable.Range(1, 8)
            .Select(seed => string.Join(",", QuizRandomizer.DrawQuestionIds(pool, seed, 8)))
            .Distinct()
            .Count();
        Assert.True(sets > 1, "different seeds should yield different drawn question sequences");
    }

    [Fact]
    public void Option_order_varies_by_seed()
    {
        // Same question, different seeds -> the displayed option order differs (so two
        // players sitting together see options in a different order).
        var orders = Enumerable.Range(1, 8)
            .Select(seed => string.Join(",", QuizRandomizer.OptionDisplayOrder(seed, questionId: 555, optionCount: 4)))
            .Distinct()
            .Count();
        Assert.True(orders > 1, "different seeds should shuffle the options differently");
    }

    [Fact]
    public void DisplayedToOriginal_inverts_the_render_order()
    {
        const int seed = 7, qid = 21, count = 4;
        var order = QuizRandomizer.OptionDisplayOrder(seed, qid, count);
        for (var displayed = 0; displayed < count; displayed++)
        {
            // The original index the engine resolves for a clicked position must equal
            // the value the render placed at that position.
            Assert.Equal(order[displayed],
                QuizRandomizer.DisplayedToOriginalIndex(seed, qid, count, displayed));
        }
        // Out-of-range displayed positions are rejected.
        Assert.Equal(-1, QuizRandomizer.DisplayedToOriginalIndex(seed, qid, count, -1));
        Assert.Equal(-1, QuizRandomizer.DisplayedToOriginalIndex(seed, qid, count, count));
    }
}
