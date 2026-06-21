using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages.Speaker;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the speaker self-service "My session ratings" page
/// (<see cref="EvaluationsModel"/>, REQUIREMENTS §20 Speaker). Drives the real
/// page model over a fake HttpContext. Proves:
///  - a signed-in speaker's OWN session evaluations load (own-row scope) and a
///    co-speaker's solo-session ratings never appear,
///  - a non-speaker role is access-denied (friendly, not the content),
///  - an anonymous user is redirected to Login.
/// FAKE names only.
/// </summary>
public sealed class SpeakerEvaluationsPageTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"spk-eval-{Guid.NewGuid():N}")
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

    private static EvaluationsModel NewModel(CommunityHubDbContext db, HttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new EvaluationsModel(
            accessor, new SpeakerEvaluationsService(db), new PublicSessionsService(db));
    }

    private sealed record Seeded(int EventId, Participant Alice, Participant Bob);

    private static async Task<Seeded> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "SPK27", CommunityName = "C", DisplayName = "SPK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        Participant Spk(string name, string email)
        {
            var p = new Participant
            {
                EventId = evt.Id, FullName = name, Email = email,
                Role = ParticipantRole.Speaker, IsActive = true,
            };
            db.Participants.Add(p);
            return p;
        }
        var alice = Spk("Alice Adams", "alice@example.test");
        var bob = Spk("Bob Brown", "bob@example.test");
        await db.SaveChangesAsync();

        Session Sess(string id, string title, params Participant[] speakers)
        {
            var s = new Session
            {
                EventId = evt.Id, SessionizeId = id, Title = title,
                Type = SessionType.CommunityTechSession, Room = "Room A",
                StartsAt = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero),
            };
            foreach (var sp in speakers)
                s.SessionSpeakers.Add(new SessionSpeaker { Session = s, Participant = sp });
            db.Sessions.Add(s);
            return s;
        }
        var aliceTalk = Sess("s-alice", "Alice Talk", alice);
        var bobSolo = Sess("s-bob", "Bob Solo", bob);
        await db.SaveChangesAsync();

        db.SessionEvaluations.Add(new SessionEvaluation
        {
            EventId = evt.Id, SessionId = aliceTalk.Id, Rating = 5,
            Comment = "Great!", CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SessionEvaluations.Add(new SessionEvaluation
        {
            EventId = evt.Id, SessionId = bobSolo.Id, Rating = 1,
            Comment = "Bob only", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return new Seeded(evt.Id, alice, bob);
    }

    [Fact]
    public async Task Loads_only_my_own_session_evaluations()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        Assert.False(model.AccessDenied);
        var only = Assert.Single(model.Result.Sessions);
        Assert.Equal("Alice Talk", only.Title);
        Assert.Equal(5.0, only.AverageRating);
        Assert.Equal(1, model.Result.TotalCount);
        Assert.DoesNotContain(model.Result.Sessions, x => x.Title == "Bob Solo");
    }

    [Fact]
    public async Task Non_speaker_role_is_access_denied()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        s.Alice.Role = ParticipantRole.Attendee;
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        Assert.True(model.AccessDenied);
        Assert.Empty(model.Result.Sessions);
    }

    [Fact]
    public async Task Anonymous_is_redirected_to_login()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var model = NewModel(db, http);

        var result = await model.OnGetAsync(default);

        var redirect = Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToPageResult>(result);
        Assert.Equal("/Login", redirect.PageName);
    }

    // BUG fix (speaker UX audit, P1): the evaluations page linked every session
    // title to /Sessions/{id} with NO gate, so an unpublished/unscheduled session
    // could land on a 404 / thin page. The title must link only when the session is
    // actually publicly viewable — the SAME gate PublicSessionsService.GetByIdAsync
    // uses (active edition + non-service session).
    [Fact]
    public async Task Session_title_links_when_session_publicly_viewable()
    {
        using var db = NewDb();
        var s = await SeedAsync(db); // Alice's talk is in the active edition.
        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        var only = Assert.Single(model.Result.Sessions);
        Assert.Contains(only.SessionId, model.PubliclyViewableSessionIds);
    }

    [Fact]
    public async Task Session_title_not_linked_when_session_not_publicly_viewable()
    {
        using var db = NewDb();
        var s = await SeedAsync(db);
        // Make Alice's session NOT publicly viewable using the SAME gate the public
        // page applies: turn it into a service session (never publicly addressable).
        var aliceTalk = await db.Sessions.FirstAsync(x => x.SessionizeId == "s-alice");
        aliceTalk.IsServiceSession = true;
        // Keep it in the evaluation result (the eval service would also drop a
        // service session); instead exercise the gate via the page model's call by
        // asserting the helper's set excludes it directly.
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        Assert.DoesNotContain(aliceTalk.Id, model.PubliclyViewableSessionIds);
    }
}
