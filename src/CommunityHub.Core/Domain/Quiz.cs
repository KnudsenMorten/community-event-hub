namespace CommunityHub.Core.Domain;

/// <summary>
/// One "fun IT games" quiz (REQUIREMENTS §171), scoped to one event edition via
/// <see cref="EventId"/> like every other domain type. A quiz owns a POOL of
/// <see cref="QuizQuestion"/>s; each attempt draws <see cref="QuestionsPerAttempt"/>
/// of them at random (anti-copy). Three are shipped per edition by the seeder —
/// AI, Intune, Security — and the organizer can edit/extend/disable them.
/// </summary>
public class Quiz
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    /// <summary>The edition this quiz belongs to. Every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The quiz topic (AI / Intune / Security).</summary>
    public QuizTopic Topic { get; set; }

    /// <summary>Human-friendly title shown on the play + leaderboard pages.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>URL-stable slug used in the player routes (<c>/Games/Play?topic=…</c>
    /// routes by <see cref="Topic"/>; the slug is the stable seed/idempotency key the
    /// seeder upserts on). Unique within an edition.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>False = hidden from players (organizer kill switch). Default true.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>How many questions each attempt DRAWS from the pool (anti-copy: a
    /// pool larger than this means two players rarely see the same set).</summary>
    public int QuestionsPerAttempt { get; set; } = 8;

    /// <summary>The per-question time budget in seconds. Drives the countdown AND the
    /// speed-decay scoring curve (a correct answer at t=0 earns the full base; at the
    /// budget it earns ~0).</summary>
    public int PerQuestionSeconds { get; set; } = 20;

    /// <summary>Points a correct answer earns when answered instantly; the speed-decay
    /// curve scales this down toward 0 as the per-question budget elapses.</summary>
    public int BasePoints { get; set; } = 1000;

    /// <summary>Display order on the Games index (lower first).</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // --- Navigation ---------------------------------------------------------
    public ICollection<QuizQuestion> Questions { get; set; } = new List<QuizQuestion>();
}
