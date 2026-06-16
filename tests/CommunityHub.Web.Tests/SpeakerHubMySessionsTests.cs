using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages.Speaker;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Web tests for the Speaker hub (<see cref="IndexModel"/>, REQUIREMENTS §20
/// Speaker — "self-service … 'My sessions' … public preview"). Drives the real
/// page model over a fake HttpContext. Proves:
///  - the signed-in speaker's OWN sessions are loaded (own-row scope) and a
///    co-speaker's solo session never appears,
///  - the PUBLIC-preview affordance is gated on SelectedForPublish (the §6 hard
///    gate): pending until the organizer selects the speaker, live afterwards.
/// FAKE names only.
/// </summary>
public sealed class SpeakerHubMySessionsTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"spk-hub-{Guid.NewGuid():N}")
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

    private static IndexModel NewModel(CommunityHubDbContext db, DefaultHttpContext http)
    {
        http.Request.Host = new HostString("ceh.example.test");
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        // Point the deadline seeder at a non-existent config so seeding is a
        // harmless no-op (the page swallows seeding errors anyway).
        var seeder = new SpeakerDeadlineSeeder(
            db, new SpeakerDeadlineOptions { ConfigPath = "does-not-exist.json" },
            TimeProvider.System);

        var model = new IndexModel(
            db,
            accessor,
            new SpeakerMilestoneService(db, TimeProvider.System),
            seeder,
            new MasterClassLogisticsService(db, TimeProvider.System),
            new SpeakerSessionsService(db),
            new CalendarFeedTokenService(db),
            NullLogger<IndexModel>.Instance);

        var actionContext = new ActionContext(
            http, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary());
        model.PageContext = new PageContext(actionContext);
        // A minimal UrlHelper so Url.Page(...) doesn't NRE when the profile is published.
        model.Url = new FakeUrlHelper(actionContext);
        return model;
    }

    private sealed class FakeUrlHelper(ActionContext ctx) : IUrlHelper
    {
        public ActionContext ActionContext { get; } = ctx;
        public string? Action(UrlActionContext c) => "/";
        public string? Content(string? p) => p;
        public bool IsLocalUrl(string? url) => true;
        public string? Link(string? name, object? values) => "/";
        public string? RouteUrl(UrlRouteContext c) => "/Speakers/1";
    }

    private sealed record Seeded(int EventId, Participant Alice, Participant Bob, SpeakerProfile AliceProfile);

    private static async Task<Seeded> SeedAsync(CommunityHubDbContext db, bool aliceSelected)
    {
        var evt = new Event
        {
            Code = "SPK27", CommunityName = "C", DisplayName = "SPK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true, CalendarSyncEnabled = false,
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

        var profile = new SpeakerProfile
        {
            EventId = evt.Id, ParticipantId = alice.Id,
            SelectedForPublish = aliceSelected, Biography = "Bio.",
        };
        db.SpeakerProfiles.Add(profile);

        Session Sess(string id, string title, params Participant[] speakers)
        {
            var s = new Session
            {
                EventId = evt.Id, SessionizeId = id, Title = title,
                Type = SessionType.CommunityTechSession,
                StartsAt = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero),
            };
            foreach (var sp in speakers)
                s.SessionSpeakers.Add(new SessionSpeaker { Session = s, Participant = sp });
            db.Sessions.Add(s);
            return s;
        }
        Sess("s-alice", "Alice Talk", alice);
        Sess("s-bob", "Bob Solo", bob);
        await db.SaveChangesAsync();

        return new Seeded(evt.Id, alice, bob, profile);
    }

    [Fact]
    public async Task Loads_only_my_own_sessions()
    {
        using var db = NewDb();
        var s = await SeedAsync(db, aliceSelected: false);
        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        Assert.False(model.AccessDenied);
        var mine = Assert.Single(model.MySessions);
        Assert.Equal("Alice Talk", mine.Title);
    }

    [Fact]
    public async Task Public_preview_is_pending_until_selected_for_publish()
    {
        using var db = NewDb();
        var s = await SeedAsync(db, aliceSelected: false);
        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        Assert.False(model.PublicProfileLive);
        Assert.Equal(string.Empty, model.PublicPreviewUrl);
    }

    [Fact]
    public async Task Public_preview_goes_live_once_selected_for_publish()
    {
        using var db = NewDb();
        var s = await SeedAsync(db, aliceSelected: true);
        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        Assert.True(model.PublicProfileLive);
        Assert.False(string.IsNullOrEmpty(model.PublicPreviewUrl));
    }
}
