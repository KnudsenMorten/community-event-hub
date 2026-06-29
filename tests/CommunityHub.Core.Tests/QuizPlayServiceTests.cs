using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Quizzes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §171 play engine — proves it is SERVER-AUTHORITATIVE: the draw is N distinct
/// questions, options are shuffled per attempt, scoring + timing come from the
/// server clock (the client only posts a displayed-option position and cannot supply
/// a score/time), out-of-turn/foreign submits are rejected, and finishing finalizes
/// the attempt with the summed score.
/// </summary>
public sealed class QuizPlayServiceTests
{
    private const int EventId = 11;

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-06-29T09:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(int ms) => Now = Now.AddMilliseconds(ms);
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"quizplay-{Guid.NewGuid():N}").Options);

    private static async Task<int> SeedParticipantAsync(CommunityHubDbContext db, string name = "Alice Tester")
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{Guid.NewGuid():N}@example.test",
            Role = ParticipantRole.Attendee, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task<(CommunityHubDbContext db, MutableClock clock, QuizPlayService svc, int pid, Quiz quiz)>
        ArrangeAsync()
    {
        var db = NewDb();
        var clock = new MutableClock();
        await new QuizSeeder(db, clock).SeedAsync(EventId);
        var svc = new QuizPlayService(db, clock);
        var pid = await SeedParticipantAsync(db);
        var quiz = await db.Quizzes.FirstAsync(q => q.EventId == EventId && q.Topic == QuizTopic.Ai);
        return (db, clock, svc, pid, quiz);
    }

    // Resolve the DISPLAYED position of the correct option for the current step,
    // re-deriving the same shuffle the engine used (server-side mapping).
    private static async Task<int> CorrectDisplayedIndexAsync(CommunityHubDbContext db, int attemptId, int questionId)
    {
        var attempt = await db.QuizAttempts.AsNoTracking().FirstAsync(a => a.Id == attemptId);
        var q = await db.QuizQuestions.AsNoTracking().FirstAsync(x => x.Id == questionId);
        var order = QuizRandomizer.OptionDisplayOrder(attempt.Seed, q.Id, q.Options.Count);
        return Array.IndexOf(order, q.CorrectIndex);
    }

    [Fact]
    public async Task Start_draws_N_distinct_questions_with_shuffled_options()
    {
        var (db, _, svc, pid, quiz) = await ArrangeAsync();
        using var _db = db;

        var attempt = await svc.StartNewAttemptAsync(EventId, pid, quiz.Id, seedOverride: 1234);
        Assert.NotNull(attempt);

        var answers = await db.QuizAttemptAnswers
            .Where(a => a.QuizAttemptId == attempt!.Id).ToListAsync();
        Assert.Equal(quiz.QuestionsPerAttempt, answers.Count);
        Assert.Equal(answers.Count, answers.Select(a => a.QuestionId).Distinct().Count()); // distinct draw
        Assert.Equal(answers.Count, answers.Select(a => a.OrderIndex).Distinct().Count()); // ordered 0..N-1

        var step = await svc.GetCurrentStepAsync(attempt!.Id, pid);
        Assert.NotNull(step);
        var question = await db.QuizQuestions.FirstAsync(q => q.Id == step!.QuestionId);
        // The displayed options are exactly the original set, just re-ordered (anti-copy).
        Assert.Equal(question.Options.OrderBy(x => x), step!.DisplayedOptions.OrderBy(x => x));
    }

    [Fact]
    public async Task Score_and_timing_come_from_the_server_clock_not_the_client()
    {
        var (db, clock, svc, pid, quiz) = await ArrangeAsync();
        using var _db = db;

        var attempt = await svc.StartNewAttemptAsync(EventId, pid, quiz.Id, seedOverride: 55);

        // Q1: answered FAST (200 ms after shown).
        var s1 = await svc.GetCurrentStepAsync(attempt!.Id, pid);   // stamps ShownAt = now
        clock.Advance(200);
        var fastIdx = await CorrectDisplayedIndexAsync(db, attempt.Id, s1!.QuestionId);
        var r1 = await svc.SubmitAnswerAsync(attempt.Id, pid, s1.QuestionId, fastIdx);

        // Q2: answered SLOW (18 000 ms — near the 20 s budget).
        var s2 = await svc.GetCurrentStepAsync(attempt.Id, pid);
        clock.Advance(18_000);
        var slowIdx = await CorrectDisplayedIndexAsync(db, attempt.Id, s2!.QuestionId);
        var r2 = await svc.SubmitAnswerAsync(attempt.Id, pid, s2.QuestionId, slowIdx);

        Assert.True(r1!.IsCorrect);
        Assert.True(r2!.IsCorrect);
        // Both correct, but the FAST answer scored much higher — purely from server timing.
        Assert.True(r1.PointsAwarded > r2.PointsAwarded,
            $"fast {r1.PointsAwarded} should beat slow {r2.PointsAwarded}");
        Assert.True(r1.PointsAwarded >= 950, $"a near-instant correct answer earns close to the base; got {r1.PointsAwarded}");

        // The persisted score is the server's sum — there is no client score input at all.
        var stored = await db.QuizAttempts.FirstAsync(a => a.Id == attempt.Id);
        Assert.Equal(r1.PointsAwarded + r2.PointsAwarded, stored.Score);
    }

    [Fact]
    public async Task Wrong_answer_scores_zero()
    {
        var (db, clock, svc, pid, quiz) = await ArrangeAsync();
        using var _db = db;

        var attempt = await svc.StartNewAttemptAsync(EventId, pid, quiz.Id, seedOverride: 7);
        var step = await svc.GetCurrentStepAsync(attempt!.Id, pid);
        clock.Advance(1000);

        var correct = await CorrectDisplayedIndexAsync(db, attempt.Id, step!.QuestionId);
        var wrong = (correct + 1) % step.DisplayedOptions.Count;
        var r = await svc.SubmitAnswerAsync(attempt.Id, pid, step.QuestionId, wrong);

        Assert.False(r!.IsCorrect);
        Assert.Equal(0, r.PointsAwarded);
    }

    [Fact]
    public async Task Out_of_turn_or_foreign_submits_are_rejected()
    {
        var (db, _, svc, pid, quiz) = await ArrangeAsync();
        using var _db = db;

        var attempt = await svc.StartNewAttemptAsync(EventId, pid, quiz.Id, seedOverride: 9);
        var step = await svc.GetCurrentStepAsync(attempt!.Id, pid);

        // Wrong question id for the current step ⇒ rejected (anti-replay/forgery).
        Assert.Null(await svc.SubmitAnswerAsync(attempt.Id, pid, questionId: -999, displayedIndex: 0));

        // A different participant cannot touch this attempt (own-row scope).
        var otherPid = await SeedParticipantAsync(db, "Mallory Other");
        Assert.Null(await svc.SubmitAnswerAsync(attempt.Id, otherPid, step!.QuestionId, 0));

        // The real, current submit still works afterwards.
        var ok = await svc.SubmitAnswerAsync(attempt.Id, pid, step!.QuestionId, 0);
        Assert.NotNull(ok);
    }

    [Fact]
    public async Task Finishing_all_questions_completes_the_attempt_with_the_summed_score()
    {
        var (db, clock, svc, pid, quiz) = await ArrangeAsync();
        using var _db = db;

        var attempt = await svc.StartNewAttemptAsync(EventId, pid, quiz.Id, seedOverride: 321);

        var guard = 0;
        QuizStep? step;
        var expectedSum = 0;
        while ((step = await svc.GetCurrentStepAsync(attempt!.Id, pid)) is not null && guard++ < 50)
        {
            clock.Advance(1000);
            var idx = await CorrectDisplayedIndexAsync(db, attempt.Id, step.QuestionId);
            var r = await svc.SubmitAnswerAsync(attempt.Id, pid, step.QuestionId, idx);
            expectedSum += r!.PointsAwarded;
        }

        var stored = await db.QuizAttempts.FirstAsync(a => a.Id == attempt!.Id);
        Assert.True(stored.IsComplete);
        Assert.NotNull(stored.CompletedAt);
        Assert.Equal(expectedSum, stored.Score);
        Assert.True(stored.ElapsedMs > 0);
        // No further step is served once complete.
        Assert.Null(await svc.GetCurrentStepAsync(attempt!.Id, pid));
    }

    [Fact]
    public async Task GetOrStart_resumes_an_in_progress_attempt()
    {
        var (db, _, svc, pid, quiz) = await ArrangeAsync();
        using var _db = db;

        var first = await svc.GetOrStartAttemptAsync(EventId, pid, QuizTopic.Ai);
        var again = await svc.GetOrStartAttemptAsync(EventId, pid, QuizTopic.Ai);

        Assert.NotNull(first);
        Assert.Equal(first!.Id, again!.Id); // same unfinished attempt resumed, not a new one
    }
}
