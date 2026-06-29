namespace CommunityHub.Core.Quizzes;

/// <summary>
/// The anti-copy randomiser (REQUIREMENTS §171). Pure + deterministic: given a
/// per-attempt <c>seed</c> it (a) draws N questions from a larger pool and (b)
/// shuffles each question's answer options — so two players sitting together get a
/// different question set AND a different option order, and can't copy. Determinism
/// matters because the engine re-derives the SAME option order on render and on
/// submit (it never stores the permutation), so it can map the clicked position
/// back to the original option index server-side.
///
/// <para>The RNG is a self-contained SplitMix64 keyed by a stable hash of
/// (seed, salt) — NOT <see cref="System.Random"/> — so the order is reproducible
/// across runtimes/processes and never depends on framework internals.</para>
/// </summary>
public static class QuizRandomizer
{
    /// <summary>
    /// A reproducible permutation of <paramref name="count"/> indices (0..count-1),
    /// keyed by (<paramref name="seed"/>, <paramref name="salt"/>). Fisher–Yates over
    /// the SplitMix64 stream. The same arguments always yield the same order.
    /// </summary>
    public static int[] Permutation(int seed, int salt, int count)
    {
        var order = new int[Math.Max(0, count)];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        if (order.Length < 2) return order;

        var state = Mix((ulong)(uint)seed ^ (0x9E3779B97F4A7C15UL * (ulong)(uint)salt));
        // Fisher–Yates: walk from the end, swapping with a uniformly-chosen earlier slot.
        for (var i = order.Length - 1; i > 0; i--)
        {
            state = Next(state);
            var j = (int)(state % (ulong)(i + 1));
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }

    /// <summary>
    /// Draw up to <paramref name="n"/> distinct pool ids in a seed-shuffled order
    /// (the attempt's question set + sequence). Fewer than <paramref name="n"/> are
    /// returned when the pool is smaller; the result is always a subset with no repeats.
    /// </summary>
    public static IReadOnlyList<int> DrawQuestionIds(IReadOnlyList<int> poolIds, int seed, int n)
    {
        if (poolIds is null || poolIds.Count == 0 || n <= 0) return Array.Empty<int>();
        var perm = Permutation(seed, salt: 0, count: poolIds.Count);
        var take = Math.Min(n, poolIds.Count);
        var picked = new int[take];
        for (var i = 0; i < take; i++) picked[i] = poolIds[perm[i]];
        return picked;
    }

    /// <summary>
    /// The shuffled DISPLAY order for one question's options: element <c>k</c> is the
    /// ORIGINAL option index shown at displayed position <c>k</c>. Salted with the
    /// question id so each question in the attempt shuffles independently.
    /// </summary>
    public static int[] OptionDisplayOrder(int seed, int questionId, int optionCount) =>
        Permutation(seed, salt: questionId, count: optionCount);

    /// <summary>
    /// Map a clicked DISPLAYED option position back to the question's ORIGINAL option
    /// index, using the same seed/question salt the render used. Returns -1 when the
    /// displayed position is out of range (a malformed/forged submit).
    /// </summary>
    public static int DisplayedToOriginalIndex(int seed, int questionId, int optionCount, int displayedIndex)
    {
        if (displayedIndex < 0 || displayedIndex >= optionCount) return -1;
        return OptionDisplayOrder(seed, questionId, optionCount)[displayedIndex];
    }

    // SplitMix64 — a tiny, well-distributed deterministic generator.
    private static ulong Mix(ulong x) => Next(x);

    private static ulong Next(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
