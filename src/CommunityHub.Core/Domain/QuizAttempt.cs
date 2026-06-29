namespace CommunityHub.Core.Domain;

/// <summary>
/// One participant's attempt at a <see cref="Quiz"/> (REQUIREMENTS §171). Created
/// when the player starts; the engine draws <see cref="Quiz.QuestionsPerAttempt"/>
/// questions, persists one <see cref="QuizAttemptAnswer"/> row per drawn question
/// (so the drawn SET is fixed for the attempt), and stamps <see cref="Seed"/> —
/// the per-attempt seed that deterministically re-derives each question's shuffled
/// OPTION order on every render/submit (anti-copy, no permutation stored).
///
/// <para>The server is authoritative for correctness AND timing: each answer's
/// shown→answered span is stamped server-side and scored with the speed-decay
/// curve; the client never supplies a score or an elapsed time. The leaderboard
/// keeps each participant's BEST completed attempt per quiz (max score, then
/// fastest total time).</para>
/// </summary>
public class QuizAttempt
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int QuizId { get; set; }
    public Quiz Quiz { get; set; } = null!;

    /// <summary>The participant playing. Scopes the "one ranked attempt per
    /// participant per topic" leaderboard reduction + own-rank lookup.</summary>
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>The per-attempt randomisation seed (server-generated). Re-derives
    /// each drawn question's shuffled option order deterministically, so two players
    /// sitting together see different option sequences and can't copy.</summary>
    public int Seed { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set when the last drawn question is answered. Null = still in
    /// progress (resumable). Only completed attempts rank on the leaderboard.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The attempt score = sum of the per-question awarded points
    /// (server-computed; the client cannot set it).</summary>
    public int Score { get; set; }

    /// <summary>Total answered time in milliseconds = sum of each question's
    /// shown→answered span (the leaderboard tie-break: faster wins ties).</summary>
    public long ElapsedMs { get; set; }

    /// <summary>True once every drawn question has been answered.</summary>
    public bool IsComplete => CompletedAt is not null;

    // --- Navigation ---------------------------------------------------------
    public ICollection<QuizAttemptAnswer> Answers { get; set; } = new List<QuizAttemptAnswer>();
}
