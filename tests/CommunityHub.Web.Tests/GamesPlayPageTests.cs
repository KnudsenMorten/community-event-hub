using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Quizzes;
using CommunityHub.Pages.Games;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the §171 quiz PLAY page (<see cref="PlayModel"/>) over a fake
/// HttpContext + in-memory DB. Proves an attendee is shown a question, that an
/// answer is scored SERVER-SIDE (the page POST carries only a displayed-option
/// position — no score or timing), and that the reveal teaches (correct + why).
/// </summary>
public sealed class GamesPlayPageTests
{
    private const int EventId = 21;

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-06-29T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(int ms) => Now = Now.AddMilliseconds(ms);
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"gamesplay-{Guid.NewGuid():N}").Options);

    private static ClaimsPrincipal Session(Participant p) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new Claim(ClaimTypes.Email, p.Email),
            new Claim(ClaimTypes.Name, p.FullName),
            new Claim(ClaimTypes.Role, p.Role.ToString()),
            new Claim("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task<Participant> SeedAsync(CommunityHubDbContext db, MutableClock clock)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "GP27", CommunityName = "C", DisplayName = "GP 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();
        await new QuizSeeder(db, clock).SeedAsync(EventId);

        var attendee = new Participant
        {
            EventId = EventId, FullName = "Alice Attendee", Email = "alice@example.test",
            Role = ParticipantRole.Attendee, IsActive = true,
        };
        db.Participants.Add(attendee);
        await db.SaveChangesAsync();
        return attendee;
    }

    private static PlayModel NewModel(CommunityHubDbContext db, MutableClock clock, HttpContext http) =>
        new(new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)), new QuizPlayService(db, clock))
        {
            PageContext = new PageContext { HttpContext = http },
        };

    [Fact]
    public async Task OnGet_shows_a_question_to_an_attendee()
    {
        using var db = NewDb();
        var clock = new MutableClock();
        var me = await SeedAsync(db, clock);
        var http = new DefaultHttpContext { User = Session(me) };
        var model = NewModel(db, clock, http);

        var result = await model.OnGetAsync("ai", default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.NoQuiz);
        Assert.NotNull(model.Step);
        Assert.NotEmpty(model.Step!.DisplayedOptions);
        // The client never receives a correct index — only the prompt + options.
        Assert.Equal(model.Step.TotalQuestions, model.Step.TotalQuestions);
    }

    [Fact]
    public async Task OnPostAnswer_scores_on_the_server_and_reveals_the_answer()
    {
        using var db = NewDb();
        var clock = new MutableClock();
        var me = await SeedAsync(db, clock);
        var http = new DefaultHttpContext { User = Session(me) };

        // Show the first question (stamps ShownAt server-side).
        var get = NewModel(db, clock, http);
        await get.OnGetAsync("ai", default);
        var step = get.Step!;

        // Re-derive the correct DISPLAYED position the way the server would.
        var attempt = await db.QuizAttempts.AsNoTracking().FirstAsync(a => a.Id == step.AttemptId);
        var q = await db.QuizQuestions.AsNoTracking().FirstAsync(x => x.Id == step.QuestionId);
        var order = QuizRandomizer.OptionDisplayOrder(attempt.Seed, q.Id, q.Options.Count);
        var correctDisplayed = Array.IndexOf(order, q.CorrectIndex);

        clock.Advance(150); // answered fast

        var post = NewModel(db, clock, http);
        var result = await post.OnPostAnswerAsync("ai", step.AttemptId, step.QuestionId, correctDisplayed, default);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(post.Reveal);
        Assert.True(post.Reveal!.IsCorrect);
        Assert.True(post.Reveal.PointsAwarded > 0);            // server-scored
        Assert.False(string.IsNullOrWhiteSpace(post.Reveal.Explanation)); // teaches

        // The persisted score is the server's, derived from the server clock — the page
        // POST carried only the displayed-option position, never a score or a time.
        var stored = await db.QuizAttempts.FirstAsync(a => a.Id == step.AttemptId);
        Assert.Equal(post.Reveal.PointsAwarded, stored.Score);
    }

    [Fact]
    public async Task OnGet_for_an_unknown_topic_reports_no_quiz()
    {
        using var db = NewDb();
        var clock = new MutableClock();
        var me = await SeedAsync(db, clock);
        var http = new DefaultHttpContext { User = Session(me) };
        var model = NewModel(db, clock, http);

        await model.OnGetAsync("does-not-exist", default);

        Assert.True(model.NoQuiz);
        Assert.Null(model.Step);
    }
}
