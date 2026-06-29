using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Quizzes;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the §171 organizer authoring page (<see cref="QuizzesModel"/>):
/// the organizer sees the seeded quizzes, can add a question, and a non-organizer is
/// access-denied. FAKE names only.
/// </summary>
public sealed class OrganizerQuizzesPageTests
{
    private const int EventId = 31;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-29T11:00:00Z");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"orgquiz-{Guid.NewGuid():N}").Options);

    private static ClaimsPrincipal Session(Participant p) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new Claim(ClaimTypes.Email, p.Email),
            new Claim(ClaimTypes.Name, p.FullName),
            new Claim(ClaimTypes.Role, p.Role.ToString()),
            new Claim("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task<Participant> SeedOrganizerAsync(CommunityHubDbContext db, ParticipantRole role)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "OQ27", CommunityName = "C", DisplayName = "OQ 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = EventId, FullName = "Olivia Organizer", Email = "olivia@example.test",
            Role = role, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private static QuizzesModel NewModel(CommunityHubDbContext db, HttpContext http)
    {
        var clock = new FixedClock();
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new QuizzesModel(accessor, new QuizAuthoringService(db, clock), new QuizSeeder(db, clock))
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    [Fact]
    public async Task Organizer_sees_the_seeded_quizzes()
    {
        using var db = NewDb();
        var org = await SeedOrganizerAsync(db, ParticipantRole.Organizer);
        var http = new DefaultHttpContext { User = Session(org) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(null, default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);
        Assert.Equal(3, model.Quizzes.Count); // AI / Intune / Security, seeded idempotently
    }

    [Fact]
    public async Task Non_organizer_is_access_denied()
    {
        using var db = NewDb();
        var attendee = await SeedOrganizerAsync(db, ParticipantRole.Attendee);
        var http = new DefaultHttpContext { User = Session(attendee) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(null, default);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
        Assert.Empty(model.Quizzes);
    }

    [Fact]
    public async Task Organizer_can_add_a_question_to_a_quiz()
    {
        using var db = NewDb();
        var org = await SeedOrganizerAsync(db, ParticipantRole.Organizer);
        await new QuizSeeder(db, new FixedClock()).SeedAsync(EventId);
        var quiz = await db.Quizzes.FirstAsync(q => q.Topic == QuizTopic.Security);
        var before = await db.QuizQuestions.CountAsync(x => x.QuizId == quiz.Id);

        var http = new DefaultHttpContext { User = Session(org) };
        var model = NewModel(db, http);

        var result = await model.OnPostAddQuestionAsync(
            quiz.Id, "What is a passphrase?", "A long memorable password", "A short PIN", "A username", null,
            correctIndex: 0, explanation: "Length beats complexity.", default);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(before + 1, await db.QuizQuestions.CountAsync(x => x.QuizId == quiz.Id));
        var added = await db.QuizQuestions.FirstAsync(x => x.Prompt == "What is a passphrase?");
        Assert.Equal(3, added.Options.Count); // trailing blank 4th option dropped
        Assert.Equal(0, added.CorrectIndex);
    }
}
