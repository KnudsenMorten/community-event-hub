using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Quizzes;

/// <summary>A quiz plus its pool size, for the organizer authoring list.</summary>
public sealed record QuizSummary(Quiz Quiz, int ActiveQuestionCount, int TotalQuestionCount, int AttemptCount);

/// <summary>
/// Organizer authoring for the §171 quizzes — view/add/edit/disable quizzes and
/// their questions. Edition-scoped (every op verifies the quiz/question belongs to
/// the organizer's edition) and clock-stamped so the GUI shows honest "updated"
/// times. The seeded starter pool is fully editable through here.
/// </summary>
public sealed class QuizAuthoringService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public QuizAuthoringService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Every quiz in the edition with its pool + attempt counts (topic order).</summary>
    public async Task<IReadOnlyList<QuizSummary>> GetQuizzesAsync(int eventId, CancellationToken ct = default)
    {
        var quizzes = await _db.Quizzes
            .Where(q => q.EventId == eventId)
            .OrderBy(q => q.SortOrder).ThenBy(q => q.Topic)
            .ToListAsync(ct);

        var result = new List<QuizSummary>();
        foreach (var q in quizzes)
        {
            var total = await _db.QuizQuestions.CountAsync(x => x.QuizId == q.Id, ct);
            var active = await _db.QuizQuestions.CountAsync(x => x.QuizId == q.Id && x.IsActive, ct);
            var attempts = await _db.QuizAttempts.CountAsync(x => x.QuizId == q.Id && x.CompletedAt != null, ct);
            result.Add(new QuizSummary(q, active, total, attempts));
        }
        return result;
    }

    /// <summary>One quiz with its questions (authoring order), scoped to the edition.</summary>
    public async Task<Quiz?> GetQuizAsync(int eventId, int quizId, CancellationToken ct = default)
    {
        var quiz = await _db.Quizzes
            .Include(q => q.Questions.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
            .FirstOrDefaultAsync(q => q.Id == quizId && q.EventId == eventId, ct);
        return quiz;
    }

    /// <summary>Update a quiz's settings (title + play knobs + active flag).</summary>
    public async Task<bool> UpdateQuizAsync(
        int eventId, int quizId, string title, bool isActive,
        int questionsPerAttempt, int perQuestionSeconds, int basePoints, CancellationToken ct = default)
    {
        var quiz = await _db.Quizzes.FirstOrDefaultAsync(q => q.Id == quizId && q.EventId == eventId, ct);
        if (quiz is null) return false;

        quiz.Title = (title ?? string.Empty).Trim();
        quiz.IsActive = isActive;
        // Clamp to sane minimums so a typo can't make the engine undividable / unplayable.
        quiz.QuestionsPerAttempt = Math.Max(1, questionsPerAttempt);
        quiz.PerQuestionSeconds = Math.Max(5, perQuestionSeconds);
        quiz.BasePoints = Math.Max(1, basePoints);
        quiz.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Toggle a quiz on/off (organizer kill switch).</summary>
    public async Task<bool> SetQuizActiveAsync(int eventId, int quizId, bool isActive, CancellationToken ct = default)
    {
        var quiz = await _db.Quizzes.FirstOrDefaultAsync(q => q.Id == quizId && q.EventId == eventId, ct);
        if (quiz is null) return false;
        quiz.IsActive = isActive;
        quiz.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Add a new question to a quiz's pool. Returns the question id, or null
    /// when the quiz is not in the edition or the input is invalid.</summary>
    public async Task<int?> AddQuestionAsync(
        int eventId, int quizId, string prompt, IReadOnlyList<string> options,
        int correctIndex, string explanation, CancellationToken ct = default)
    {
        var quiz = await _db.Quizzes.FirstOrDefaultAsync(q => q.Id == quizId && q.EventId == eventId, ct);
        if (quiz is null || !IsValid(prompt, options, correctIndex)) return null;

        var nextOrder = (await _db.QuizQuestions
            .Where(x => x.QuizId == quizId)
            .Select(x => (int?)x.SortOrder)
            .MaxAsync(ct) ?? -1) + 1;

        var q = new QuizQuestion
        {
            QuizId = quizId,
            Prompt = prompt.Trim(),
            Options = Clean(options),
            CorrectIndex = correctIndex,
            Explanation = (explanation ?? string.Empty).Trim(),
            IsActive = true,
            SortOrder = nextOrder,
        };
        _db.QuizQuestions.Add(q);
        quiz.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return q.Id;
    }

    /// <summary>Edit an existing question (prompt/options/correct/explanation/active).</summary>
    public async Task<bool> UpdateQuestionAsync(
        int eventId, int questionId, string prompt, IReadOnlyList<string> options,
        int correctIndex, string explanation, bool isActive, CancellationToken ct = default)
    {
        var q = await _db.QuizQuestions
            .Include(x => x.Quiz)
            .FirstOrDefaultAsync(x => x.Id == questionId && x.Quiz.EventId == eventId, ct);
        if (q is null || !IsValid(prompt, options, correctIndex)) return false;

        q.Prompt = prompt.Trim();
        q.Options = Clean(options);
        q.CorrectIndex = correctIndex;
        q.Explanation = (explanation ?? string.Empty).Trim();
        q.IsActive = isActive;
        q.Quiz.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Enable/disable a single question without deleting it.</summary>
    public async Task<bool> SetQuestionActiveAsync(int eventId, int questionId, bool isActive, CancellationToken ct = default)
    {
        var q = await _db.QuizQuestions
            .Include(x => x.Quiz)
            .FirstOrDefaultAsync(x => x.Id == questionId && x.Quiz.EventId == eventId, ct);
        if (q is null) return false;
        q.IsActive = isActive;
        q.Quiz.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // A question needs a prompt, ≥2 NON-BLANK options, and a correct index inside the
    // option range. Blanks are NOT dropped (that would shift CorrectIndex): every
    // presented option must be filled, so the correct index keeps pointing at the
    // option the author chose.
    private static bool IsValid(string? prompt, IReadOnlyList<string>? options, int correctIndex)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var cleaned = Clean(options);
        return cleaned.Count >= 2
               && cleaned.All(o => o.Length > 0)
               && correctIndex >= 0 && correctIndex < cleaned.Count;
    }

    // Trim each option but PRESERVE position/count (so CorrectIndex stays aligned).
    private static List<string> Clean(IReadOnlyList<string>? options) =>
        (options ?? Array.Empty<string>())
            .Select(o => (o ?? string.Empty).Trim())
            .ToList();
}
