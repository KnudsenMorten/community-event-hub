using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Attendees;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages.Attendee;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the attendee self-service "My plan" page (<see cref="MyPlanModel"/>).
/// Drives the real page model over a fake HttpContext. Proves:
///  - a signed-in participant's OWN saved sessions load (own-row scope) — another
///    person's saved talks never appear,
///  - the Toggle handler saves then removes the same session (round-trip),
///  - an anonymous user is redirected to Login on GET and on POST.
/// FAKE names only.
/// </summary>
public sealed class AttendeeMyPlanPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"myplan-{Guid.NewGuid():N}")
            .Options);

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static ClaimsPrincipal Session(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static MyPlanModel NewModel(CommunityHubDbContext db, HttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var model = new MyPlanModel(accessor, new AttendeePlanService(db), new PublicSessionsService(db));
        model.PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext
        {
            HttpContext = http,
        };
        return model;
    }

    private sealed record Seeded(int EventId, Participant Alice, Participant Bob, int TalkA, int TalkB);

    private static async Task<Seeded> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "MP27", CommunityName = "C", DisplayName = "MP 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        Participant Att(string name, string email)
        {
            var p = new Participant
            {
                EventId = evt.Id, FullName = name, Email = email,
                Role = ParticipantRole.Attendee, IsActive = true,
            };
            db.Participants.Add(p);
            return p;
        }
        var alice = Att("Alice Adams", "alice@example.test");
        var bob = Att("Bob Brown", "bob@example.test");
        await db.SaveChangesAsync();

        Session Sess(string id, string title, DateTimeOffset start)
        {
            var s = new Session
            {
                EventId = evt.Id, SessionizeId = id, Title = title,
                Type = SessionType.TechnicalSession, Length = SessionLength.FiftyMin,
                Room = "Room A", StartsAt = start, EndsAt = start.AddMinutes(50),
            };
            db.Sessions.Add(s);
            return s;
        }
        var talkA = Sess("a", "Alpha Talk", new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero));
        var talkB = Sess("b", "Beta Talk", new DateTimeOffset(2027, 2, 9, 11, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        return new Seeded(evt.Id, alice, bob, talkA.Id, talkB.Id);
    }

    [Fact]
    public async Task Loads_only_my_own_saved_sessions()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        // Alice saved Alpha; Bob saved Beta.
        db.SavedSessions.Add(new SavedSession { EventId = s.EventId, ParticipantId = s.Alice.Id, SessionId = s.TalkA, CreatedAt = DateTimeOffset.UtcNow });
        db.SavedSessions.Add(new SavedSession { EventId = s.EventId, ParticipantId = s.Bob.Id, SessionId = s.TalkB, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        var only = Assert.Single(model.Plan.Sessions);
        Assert.Equal("Alpha Talk", only.Title);
        Assert.Contains(s.TalkA, model.SavedIds);
        Assert.DoesNotContain(s.TalkB, model.SavedIds);
        // Both talks are browsable to add.
        Assert.NotNull(model.Browse);
        Assert.Equal(2, model.Browse!.Sessions.Count);
    }

    [Fact]
    public async Task Toggle_saves_then_removes()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        var r1 = await model.OnPostToggleAsync(s.TalkA, default);
        Assert.IsType<RedirectToPageResult>(r1);
        Assert.True(await db.SavedSessions.AnyAsync(x => x.ParticipantId == s.Alice.Id && x.SessionId == s.TalkA));

        await model.OnPostToggleAsync(s.TalkA, default);
        Assert.False(await db.SavedSessions.AnyAsync(x => x.ParticipantId == s.Alice.Id && x.SessionId == s.TalkA));
    }

    [Fact]
    public async Task Remove_handler_takes_a_talk_out_of_the_plan()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        db.SavedSessions.Add(new SavedSession { EventId = s.EventId, ParticipantId = s.Alice.Id, SessionId = s.TalkA, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnPostRemoveAsync(s.TalkA, default);

        Assert.False(await db.SavedSessions.AnyAsync(x => x.ParticipantId == s.Alice.Id));
    }

    [Fact]
    public async Task Anonymous_is_redirected_to_login_on_get_and_post()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var model = NewModel(db, http);

        var get = await model.OnGetAsync(default);
        var post = await model.OnPostToggleAsync(1, default);

        Assert.Equal("/Login", Assert.IsType<RedirectToPageResult>(get).PageName);
        Assert.Equal("/Login", Assert.IsType<RedirectToPageResult>(post).PageName);
    }

    [Fact]
    public async Task Ics_handler_returns_my_scheduled_plan_as_a_calendar_file()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        db.SavedSessions.Add(new SavedSession { EventId = s.EventId, ParticipantId = s.Alice.Id, SessionId = s.TalkA, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        var result = await model.OnGetIcsAsync(default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/calendar; charset=utf-8", file.ContentType);
        Assert.Equal("my-plan.ics", file.FileDownloadName);
        var body = System.Text.Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("BEGIN:VCALENDAR", body);
        Assert.Contains("SUMMARY:Alpha Talk", body);
    }

    [Fact]
    public async Task HasScheduled_is_set_on_get_when_a_scheduled_talk_is_saved()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        db.SavedSessions.Add(new SavedSession { EventId = s.EventId, ParticipantId = s.Alice.Id, SessionId = s.TalkA, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        // Drives whether the "download calendar file" button renders on the page.
        Assert.True(model.HasScheduled);
    }

    [Fact]
    public async Task Ics_handler_404s_when_nothing_scheduled_is_saved()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        // Plan is empty → nothing to put on a calendar.
        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        var result = await model.OnGetIcsAsync(default);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Ics_handler_redirects_anonymous_to_login()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var model = NewModel(db, http);

        var result = await model.OnGetIcsAsync(default);
        Assert.Equal("/Login", Assert.IsType<RedirectToPageResult>(result).PageName);
    }
}
