using System.Linq;
using System.Threading.Tasks;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §171 starter-pool seeder. Locks: a fresh DB gets three playable quizzes
/// (AI / Intune / Security), each with a ≥10-question pool LARGER than the per-attempt
/// draw (so the random draw is meaningful), every question well-formed; and the seed
/// is idempotent (a second run adds nothing, never clobbering organizer edits).
/// </summary>
public sealed class QuizSeederTests
{
    private const int EventId = 5;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-29T08:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"quizseed-{System.Guid.NewGuid():N}").Options);

    [Fact]
    public async Task Seeds_three_topic_quizzes_with_pools()
    {
        using var db = NewDb();
        var created = await new QuizSeeder(db, new FixedClock()).SeedAsync(EventId);

        Assert.Equal(3, created);
        var quizzes = await db.Quizzes.Where(q => q.EventId == EventId).ToListAsync();
        Assert.Equal(3, quizzes.Count);

        // All three shipped topics are present and active.
        Assert.Equal(
            new[] { QuizTopic.Ai, QuizTopic.Intune, QuizTopic.Security }.OrderBy(t => t),
            quizzes.Select(q => q.Topic).OrderBy(t => t));
        Assert.All(quizzes, q => Assert.True(q.IsActive));
    }

    [Fact]
    public async Task Each_topic_pool_has_at_least_ten_questions_larger_than_the_draw()
    {
        using var db = NewDb();
        await new QuizSeeder(db, new FixedClock()).SeedAsync(EventId);

        foreach (var quiz in await db.Quizzes.Where(q => q.EventId == EventId).ToListAsync())
        {
            var count = await db.QuizQuestions.CountAsync(x => x.QuizId == quiz.Id && x.IsActive);
            Assert.True(count >= 10, $"{quiz.Topic} should ship ≥10 questions; got {count}");
            // Pool must exceed the per-attempt draw, else there is nothing to randomise.
            Assert.True(count > quiz.QuestionsPerAttempt,
                $"{quiz.Topic} pool ({count}) must be larger than the draw ({quiz.QuestionsPerAttempt})");
        }
    }

    [Fact]
    public async Task Every_seeded_question_is_well_formed()
    {
        using var db = NewDb();
        await new QuizSeeder(db, new FixedClock()).SeedAsync(EventId);

        foreach (var q in await db.QuizQuestions.ToListAsync())
        {
            var options = q.Options;
            Assert.True(options.Count >= 2, "needs at least two options");
            Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o)));
            Assert.InRange(q.CorrectIndex, 0, options.Count - 1);
            Assert.False(string.IsNullOrWhiteSpace(q.Prompt));
            Assert.False(string.IsNullOrWhiteSpace(q.Explanation));
        }
    }

    [Fact]
    public async Task Seeding_is_idempotent()
    {
        using var db = NewDb();
        var clock = new FixedClock();
        await new QuizSeeder(db, clock).SeedAsync(EventId);
        var firstCount = await db.QuizQuestions.CountAsync();

        var createdAgain = await new QuizSeeder(db, clock).SeedAsync(EventId);

        Assert.Equal(0, createdAgain);
        Assert.Equal(3, await db.Quizzes.CountAsync(q => q.EventId == EventId));
        Assert.Equal(firstCount, await db.QuizQuestions.CountAsync()); // no duplicate questions
    }
}
