using System.Text;
using CommunityHub.Api;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// The PUBLIC, no-login per-session calendar download (REQUIREMENTS §21 PUBLIC):
/// <c>GET /Sessions/{id}.ics</c> via <see cref="PublicSessionCalendarController"/>.
/// Proves the endpoint serves anonymously (no auth on the controller path), returns a
/// <c>text/calendar</c> file for a scheduled public session, and 404s for an
/// unscheduled / service / unknown session so nothing private leaks. Drives the real
/// controller over a fake HttpContext (in-memory DB). FAKE names only.
/// </summary>
public sealed class PublicSessionCalendarControllerTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"sess-ics-{Guid.NewGuid():N}")
            .Options);

    private static PublicSessionCalendarController NewController(CommunityHubDbContext db)
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("ceh.example.test");
        return new PublicSessionCalendarController(new PublicSessionsService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static async Task<(int scheduledId, int unscheduledId, int serviceId)> SeedAsync(
        CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "PUB27", CommunityName = "Public Community",
            DisplayName = "Public Community 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            VenueName = "Test Venue", IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var spk = new Participant
        {
            EventId = evt.Id, FullName = "Alice Adams", Email = "alice@example.test",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(spk);
        await db.SaveChangesAsync();

        var scheduled = new Session
        {
            EventId = evt.Id, SessionizeId = "sess-1", Title = "Intro to Bicep",
            Abstract = "A talk.", Type = SessionType.TechnicalSession,
            Length = SessionLength.FiftyMin, Room = "Room B",
            StartsAt = new DateTimeOffset(2027, 2, 9, 11, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2027, 2, 9, 11, 50, 0, TimeSpan.Zero),
        };
        scheduled.SessionSpeakers.Add(new SessionSpeaker { Session = scheduled, Participant = spk });

        var unscheduled = new Session
        {
            EventId = evt.Id, SessionizeId = "sess-2", Title = "To Be Announced",
            Type = SessionType.TechnicalSession, Length = SessionLength.FiftyMin,
        };
        var service = new Session
        {
            EventId = evt.Id, SessionizeId = "sess-3", Title = "Coffee Break",
            Type = SessionType.TechnicalSession, Length = SessionLength.TwentyMin,
            StartsAt = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero),
            IsServiceSession = true,
        };
        db.Sessions.AddRange(scheduled, unscheduled, service);
        await db.SaveChangesAsync();
        return (scheduled.Id, unscheduled.Id, service.Id);
    }

    [Fact]
    public async Task Scheduled_session_returns_anonymous_text_calendar_file()
    {
        using var db = NewDb();
        var (scheduledId, _, _) = await SeedAsync(db);
        var controller = NewController(db);

        var result = await controller.GetSessionIcsAsync(scheduledId, default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.StartsWith("text/calendar", file.ContentType);
        var body = Encoding.UTF8.GetString(file.FileContents);
        Assert.StartsWith("BEGIN:VCALENDAR", body);
        Assert.Contains("SUMMARY:Intro to Bicep", body);
        Assert.Contains("LOCATION:Room B\\, Test Venue", body);   // room + venue, RFC-escaped
        Assert.Equal("no-store, max-age=0",
            controller.Response.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public async Task Unscheduled_service_and_unknown_sessions_return_404()
    {
        using var db = NewDb();
        var (_, unscheduledId, serviceId) = await SeedAsync(db);
        var controller = NewController(db);

        Assert.IsType<NotFoundResult>(await controller.GetSessionIcsAsync(unscheduledId, default));
        Assert.IsType<NotFoundResult>(await controller.GetSessionIcsAsync(serviceId, default));
        Assert.IsType<NotFoundResult>(await controller.GetSessionIcsAsync(999999, default));
    }
}
