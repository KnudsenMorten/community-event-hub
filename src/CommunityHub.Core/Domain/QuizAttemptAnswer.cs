namespace CommunityHub.Core.Domain;

/// <summary>
/// One drawn question within a <see cref="QuizAttempt"/> (REQUIREMENTS §171). The
/// full set of these rows IS the attempt's drawn question sequence (fixed at start,
/// ordered by <see cref="OrderIndex"/>). The engine stamps <see cref="ShownAt"/>
/// server-side when the question is rendered and <see cref="AnsweredAt"/> when the
/// player submits, so the speed-decay score is computed from server clocks only —
/// the browser never supplies timing or correctness.
/// </summary>
public class QuizAttemptAnswer
{
    public int Id { get; set; }

    public int QuizAttemptId { get; set; }
    public QuizAttempt QuizAttempt { get; set; } = null!;

    /// <summary>The drawn question.</summary>
    public int QuestionId { get; set; }
    public QuizQuestion Question { get; set; } = null!;

    /// <summary>Zero-based position of this question in the attempt's drawn
    /// sequence (the play order). Unique within an attempt.</summary>
    public int OrderIndex { get; set; }

    /// <summary>When the engine first rendered this question to the player
    /// (server clock). Null until shown. The speed timer starts here.</summary>
    public DateTimeOffset? ShownAt { get; set; }

    /// <summary>When the player submitted an answer (server clock). Null until
    /// answered.</summary>
    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>The selected option index in the question's ORIGINAL option order
    /// (the engine maps the shuffled displayed position back to the original index
    /// before storing). Null until answered.</summary>
    public int? SelectedIndex { get; set; }

    /// <summary>True when <see cref="SelectedIndex"/> equals the question's
    /// CorrectIndex. False until answered (and for a wrong/expired answer).</summary>
    public bool IsCorrect { get; set; }

    /// <summary>Server-computed points for this answer (0 when wrong or out of
    /// time; the speed-decay curve otherwise).</summary>
    public int PointsAwarded { get; set; }

    /// <summary>Shown→answered span in milliseconds (server-computed).</summary>
    public long ElapsedMs { get; set; }

    /// <summary>True once the player has submitted an answer for this question.</summary>
    public bool IsAnswered => AnsweredAt is not null;
}
