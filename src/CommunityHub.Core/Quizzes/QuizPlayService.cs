using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Quizzes;

/// <summary>The current question to render to the player (the client gets the prompt
/// + the SHUFFLED options only — never the correct index or any timing/score).</summary>
public sealed record QuizStep(
    int AttemptId,
    int QuestionId,
    int QuestionNumber,
    int TotalQuestions,
    string Prompt,
    IReadOnlyList<string> DisplayedOptions,
    int PerQuestionSeconds);

/// <summary>The reveal returned after a server-scored answer (the LEARNING step):
/// self-contained so the view can re-render the just-answered question with the
/// correct option highlighted + the explanation, without re-querying.</summary>
public sealed record QuizAnswerResult(
    bool IsCorrect,
    int SelectedDisplayedIndex,
    int CorrectDisplayedIndex,
    string Prompt,
    IReadOnlyList<string> DisplayedOptions,
    string Explanation,
    int PointsAwarded,
    long ElapsedMs,
    int QuestionNumber,
    int TotalQuestions,
    bool IsLast,
    int RunningScore);

/// <summary>
/// The §171 play engine — SERVER-AUTHORITATIVE for question draw, correctness AND
/// timing. The browser only ever receives the prompt + the per-attempt shuffled
/// options; it submits a displayed-option position, and the server maps it back to
/// the original option, stamps shown→answered from its OWN clock, scores with the
/// speed-decay curve and persists. The client cannot supply a score or an elapsed
/// time — there is no input for it.
/// </summary>
public sealed class QuizPlayService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public QuizPlayService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The active quiz for a topic in an edition (null = none playable).</summary>
    public Task<Quiz?> GetActiveQuizAsync(int eventId, QuizTopic topic, CancellationToken ct = default) =>
        _db.Quizzes.FirstOrDefaultAsync(
            q => q.EventId == eventId && q.Topic == topic && q.IsActive, ct);

    /// <summary>The active quiz by slug (the player route resolves by slug/topic).</summary>
    public Task<Quiz?> GetActiveQuizBySlugAsync(int eventId, string slug, CancellationToken ct = default) =>
        _db.Quizzes.FirstOrDefaultAsync(
            q => q.EventId == eventId && q.Slug == slug && q.IsActive, ct);

    /// <summary>
    /// Resume the participant's IN-PROGRESS attempt for the topic, or start a fresh
    /// one (drawing a new random question set). Returns null when there is no active
    /// quiz or the quiz has no active questions to draw.
    /// </summary>
    public async Task<QuizAttempt?> GetOrStartAttemptAsync(
        int eventId, int participantId, QuizTopic topic, CancellationToken ct = default)
    {
        var quiz = await GetActiveQuizAsync(eventId, topic, ct);
        if (quiz is null) return null;

        var inProgress = await _db.QuizAttempts
            .Where(a => a.QuizId == quiz.Id && a.ParticipantId == participantId && a.CompletedAt == null)
            .OrderByDescending(a => a.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (inProgress is not null) return inProgress;

        return await StartNewAttemptAsync(eventId, participantId, quiz.Id, ct);
    }

    /// <summary>
    /// Start a NEW attempt: draw <see cref="Quiz.QuestionsPerAttempt"/> active
    /// questions at random with a fresh per-attempt seed and persist one answer row
    /// per drawn question (the fixed draw + play sequence). Returns null when the
    /// quiz is inactive/missing or has no active questions.
    /// </summary>
    public async Task<QuizAttempt?> StartNewAttemptAsync(
        int eventId, int participantId, int quizId, CancellationToken ct = default, int? seedOverride = null)
    {
        var quiz = await _db.Quizzes
            .FirstOrDefaultAsync(q => q.Id == quizId && q.EventId == eventId && q.IsActive, ct);
        if (quiz is null) return null;

        var poolIds = await _db.QuizQuestions
            .Where(q => q.QuizId == quiz.Id && q.IsActive)
            .OrderBy(q => q.SortOrder).ThenBy(q => q.Id)
            .Select(q => q.Id)
            .ToListAsync(ct);
        if (poolIds.Count == 0) return null;

        var seed = seedOverride ?? Random.Shared.Next();
        var drawn = QuizRandomizer.DrawQuestionIds(poolIds, seed, quiz.QuestionsPerAttempt);

        var attempt = new QuizAttempt
        {
            EventId = eventId,
            QuizId = quiz.Id,
            ParticipantId = participantId,
            Seed = seed,
            StartedAt = _clock.GetUtcNow(),
        };
        for (var i = 0; i < drawn.Count; i++)
        {
            attempt.Answers.Add(new QuizAttemptAnswer
            {
                QuestionId = drawn[i],
                OrderIndex = i,
            });
        }
        _db.QuizAttempts.Add(attempt);
        await _db.SaveChangesAsync(ct);
        return attempt;
    }

    /// <summary>
    /// The current step for an attempt (the first unanswered question), with its
    /// options shuffled per the attempt seed; stamps ShownAt server-side on first
    /// render so the speed timer starts. Returns null when the attempt is unknown,
    /// not the participant's, or already complete.
    /// </summary>
    public async Task<QuizStep?> GetCurrentStepAsync(
        int attemptId, int participantId, CancellationToken ct = default)
    {
        var attempt = await LoadAttemptAsync(attemptId, participantId, ct);
        if (attempt is null || attempt.IsComplete) return null;

        var current = attempt.Answers
            .Where(a => a.AnsweredAt == null)
            .OrderBy(a => a.OrderIndex)
            .FirstOrDefault();
        if (current is null) return null;

        var question = await _db.QuizQuestions
            .FirstOrDefaultAsync(q => q.Id == current.QuestionId, ct);
        var quiz = await _db.Quizzes.FirstAsync(q => q.Id == attempt.QuizId, ct);
        if (question is null) return null;

        var options = question.Options;
        var order = QuizRandomizer.OptionDisplayOrder(attempt.Seed, question.Id, options.Count);
        var displayed = order.Select(orig => options[orig]).ToList();

        if (current.ShownAt is null)
        {
            current.ShownAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }

        return new QuizStep(
            AttemptId: attempt.Id,
            QuestionId: question.Id,
            QuestionNumber: current.OrderIndex + 1,
            TotalQuestions: attempt.Answers.Count,
            Prompt: question.Prompt,
            DisplayedOptions: displayed,
            PerQuestionSeconds: quiz.PerQuestionSeconds);
    }

    /// <summary>
    /// Submit the player's choice (a DISPLAYED option position) for the CURRENT
    /// question. The server maps it back to the original option, scores it from its
    /// own shown→now clock + the speed curve, persists, and returns the reveal
    /// (correct option + explanation). Rejects (null) an unknown/foreign/complete
    /// attempt or a submit that is not for the current question (anti-forgery /
    /// anti-replay). The client cannot influence the score or the timing.
    /// </summary>
    public async Task<QuizAnswerResult?> SubmitAnswerAsync(
        int attemptId, int participantId, int questionId, int displayedIndex, CancellationToken ct = default)
    {
        var attempt = await LoadAttemptAsync(attemptId, participantId, ct);
        if (attempt is null || attempt.IsComplete) return null;

        var current = attempt.Answers
            .Where(a => a.AnsweredAt == null)
            .OrderBy(a => a.OrderIndex)
            .FirstOrDefault();
        // Only the current (first unanswered) question may be answered, and the
        // submitted id must match it — otherwise it is a stale/forged post.
        if (current is null || current.QuestionId != questionId) return null;

        var question = await _db.QuizQuestions.FirstOrDefaultAsync(q => q.Id == questionId, ct);
        var quiz = await _db.Quizzes.FirstAsync(q => q.Id == attempt.QuizId, ct);
        if (question is null) return null;

        var now = _clock.GetUtcNow();
        var shownAt = current.ShownAt ?? now;
        var elapsedMs = Math.Max(0L, (long)(now - shownAt).TotalMilliseconds);

        var optionCount = question.Options.Count;
        var originalIndex = QuizRandomizer.DisplayedToOriginalIndex(
            attempt.Seed, question.Id, optionCount, displayedIndex);
        var isCorrect = originalIndex >= 0 && originalIndex == question.CorrectIndex;
        var points = QuizScoring.Score(isCorrect, elapsedMs, quiz.PerQuestionSeconds, quiz.BasePoints);

        current.AnsweredAt = now;
        current.SelectedIndex = originalIndex >= 0 ? originalIndex : null;
        current.IsCorrect = isCorrect;
        current.PointsAwarded = points;
        current.ElapsedMs = elapsedMs;

        attempt.Score += points;
        attempt.ElapsedMs += elapsedMs;

        var remaining = attempt.Answers.Count(a => a.AnsweredAt == null);
        var isLast = remaining == 0;
        if (isLast) attempt.CompletedAt = now;

        await _db.SaveChangesAsync(ct);

        // The reveal needs the DISPLAYED position of the correct option (+ the options
        // in the order the player saw them) so the view can highlight it.
        var order = QuizRandomizer.OptionDisplayOrder(attempt.Seed, question.Id, optionCount);
        var displayed = order.Select(orig => question.Options[orig]).ToList();
        var correctDisplayed = Array.IndexOf(order, question.CorrectIndex);

        return new QuizAnswerResult(
            IsCorrect: isCorrect,
            SelectedDisplayedIndex: displayedIndex,
            CorrectDisplayedIndex: correctDisplayed,
            Prompt: question.Prompt,
            DisplayedOptions: displayed,
            Explanation: question.Explanation,
            PointsAwarded: points,
            ElapsedMs: elapsedMs,
            QuestionNumber: current.OrderIndex + 1,
            TotalQuestions: attempt.Answers.Count,
            IsLast: isLast,
            RunningScore: attempt.Score);
    }

    private async Task<QuizAttempt?> LoadAttemptAsync(int attemptId, int participantId, CancellationToken ct)
    {
        var attempt = await _db.QuizAttempts
            .Include(a => a.Answers)
            .FirstOrDefaultAsync(a => a.Id == attemptId, ct);
        // Own-row scope: a participant may only touch their OWN attempt.
        if (attempt is null || attempt.ParticipantId != participantId) return null;
        return attempt;
    }
}
