using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests.Organizer;

/// <summary>
/// §1291 — one session's feedback on the page (operator 2026-09-21: *"how can i click on a session
/// and see all feedbacks"*). Organizer-only, and scoped to the organizer's own edition.
/// </summary>
public sealed class SessionFeedbackViewTests
{
    private const int EventId = 94;

    private sealed class FakeAccessor(CurrentParticipant? cur) : ICurrentParticipantAccessor
    {
        public CurrentParticipant? Current { get; } = cur;
    }

    private static CurrentParticipant? SignedIn(Participant p) =>
        CurrentParticipant.FromPrincipal(new ClaimsPrincipal(new ClaimsIdentity(new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme)));

    private static async Task<(SessionFeedbackViewModel Page, int EvalId)> SetUpAsync(ParticipantRole role, int sessionEventId = EventId)
    {
        var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"fbview-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event
        {
            Id = EventId, Code = "V27", CommunityName = "C", DisplayName = "V 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var me = new Participant { EventId = EventId, Email = "org@x.test", FullName = "Org", Role = role, IsActive = true };
        db.Participants.Add(me);
        var start = new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);
        var eval = new EvaluationSession
        {
            EventId = sessionEventId, Title = "Keeping identity boring", ScheduledStart = start, ScheduledEnd = start.AddHours(1),
            CollectionWindowOpensAt = start, CollectionWindowClosesAt = start.AddMinutes(90), CreatedAt = start, UpdatedAt = start,
        };
        db.EvaluationSessions.Add(eval);
        await db.SaveChangesAsync();
        db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = sessionEventId, SessionId = eval.Id, Rating = 4, Source = EvaluationResponseSources.Qr,
            CollectionTimestamp = start, ReceivedTimestamp = start, FreeText = "Really practical.",
            PositivePoint = "great-demos",
        });
        await db.SaveChangesAsync();

        var page = new SessionFeedbackViewModel(new FakeAccessor(SignedIn(me)),
            new EvaluationReportBuilder(db, new EvaluationScoreService(db), TimeProvider.System));
        return (page, eval.Id);
    }

    [Fact]
    public async Task An_organizer_sees_the_sessions_answers_picks_and_comments()
    {
        var (page, id) = await SetUpAsync(ParticipantRole.Organizer);

        var result = await page.OnGetAsync(id, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Keeping identity boring", page.Data!.SessionTitle);
        Assert.Equal(1, page.Data.Score.Responses);
        Assert.Contains("Really practical.", page.Data.Comments);
        Assert.Equal("Great demos", Assert.Single(page.Data.PositivePoints!).Label);
    }

    [Fact]
    public async Task A_non_organizer_is_refused_and_sees_no_data()
    {
        var (page, id) = await SetUpAsync(ParticipantRole.Speaker);

        await page.OnGetAsync(id, CancellationToken.None);

        Assert.True(page.AccessDenied);
        Assert.Null(page.Data);
    }

    [Fact]
    public async Task Another_editions_session_is_not_found()
    {
        var (page, id) = await SetUpAsync(ParticipantRole.Organizer, sessionEventId: EventId + 1);

        Assert.IsType<NotFoundResult>(await page.OnGetAsync(id, CancellationToken.None));
    }
}
