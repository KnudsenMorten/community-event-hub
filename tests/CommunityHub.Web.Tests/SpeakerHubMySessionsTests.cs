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
            seeder,
            new CommunityHub.Core.Participants.FormTaskReconciler(db, TimeProvider.System),
            new MasterClassLogisticsService(db, TimeProvider.System),
            new SpeakerSessionsService(db),
            new PublicSessionsService(db),
            // §124: inert QR service (null store, no folder) — no QR links shown.
            new CommunityHub.Core.Integrations.Graphics.SessionEvalsQrService(
                new CommunityHub.Core.Integrations.Graphics.NullSharePointFileStore(),
                Microsoft.Extensions.Options.Options.Create(
                    new CommunityHub.Core.Integrations.Graphics.GraphicsSharePointOptions())),
            new CommunityHub.Core.Integrations.ZohoOptions(),
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
                Type = SessionType.TechnicalSession,
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

    // NOTE: the speaker "public profile preview" (PublicProfileLive / PublicPreviewUrl)
    // was removed as dead, never-rendered code (audit P15, §38 "remove Your public
    // profile"); its two tests were deleted with it. The session-link visibility below
    // (PubliclyViewableSessionIds) is the surviving, still-rendered behaviour.

    // BUG fix (speaker UX audit, P1): the per-session "view public session page"
    // link must be gated on the SESSION's actual public visibility (the same gate
    // PublicSessionsService.GetByIdAsync uses), NOT on the speaker's own
    // profile-publish flag. A speaker whose profile is not yet published but whose
    // session is in the active edition must still be able to reach its public page.
    [Fact]
    public async Task Session_link_shows_when_session_public_even_if_profile_unpublished()
    {
        using var db = NewDb();
        // aliceSelected:false => profile NOT published, but Alice's session IS in
        // the active edition (a non-service session) => publicly viewable.
        var s = await SeedAsync(db, aliceSelected: false);
        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        var mine = Assert.Single(model.MySessions);
        // Link is driven by the session's public visibility, so it SHOWS.
        Assert.Contains(mine.SessionId, model.PubliclyViewableSessionIds);
    }

    [Fact]
    public async Task My_sessions_show_known_fallback_date_when_time_is_TBD()
    {
        // §88: the exact time is READ from CEH; until it is synced, My Sessions still shows
        // the KNOWN day — a Master Class on the pre-day (the 9th), a regular session on the
        // first main day (the 10th) — with a "TBD" time. The service exposes that day as
        // FallbackDate (null once a real StartsAt is set).
        using var db = NewDb();
        var evt = new Event
        {
            Code = "SPK27", CommunityName = "C", DisplayName = "SPK 2027",
            StartDate = new DateOnly(2027, 2, 10), EndDate = new DateOnly(2027, 2, 10),
            PreDayDate = new DateOnly(2027, 2, 9), IsActive = true, CalendarSyncEnabled = false,
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

        Session Sess(string id, string title, SessionType type)
        {
            var s = new Session
            {
                EventId = evt.Id, SessionizeId = id, Title = title, Type = type, StartsAt = null,
            };
            s.SessionSpeakers.Add(new SessionSpeaker { Session = s, Participant = spk });
            db.Sessions.Add(s);
            return s;
        }
        Sess("s-talk", "Talk", SessionType.TechnicalSession);
        Sess("s-mc", "Workshop", SessionType.MasterClass);
        await db.SaveChangesAsync();

        var sessions = await new SpeakerSessionsService(db)
            .GetMySessionsAsync(evt.Id, spk.Id, ParticipantRole.Speaker);

        var talk = sessions.Single(s => s.Title == "Talk");
        var mc = sessions.Single(s => s.Title == "Workshop");
        Assert.Null(talk.StartsAt);
        Assert.Equal(new DateOnly(2027, 2, 10), talk.FallbackDate); // session on the 10th
        Assert.Equal(new DateOnly(2027, 2, 9), mc.FallbackDate);    // master class on the 9th
    }

    [Fact]
    public async Task Fallback_date_is_null_once_the_real_time_is_set()
    {
        // When StartsAt IS set, the display uses the real time and FallbackDate is null
        // (the known-day hint is only a stand-in for an unsynced time).
        using var db = NewDb();
        var s = await SeedAsync(db, aliceSelected: false); // SeedAsync gives sessions a StartsAt
        var sessions = await new SpeakerSessionsService(db)
            .GetMySessionsAsync(s.EventId, s.Alice.Id, ParticipantRole.Speaker);
        var mine = Assert.Single(sessions);
        Assert.NotNull(mine.StartsAt);
        Assert.Null(mine.FallbackDate);
    }

    [Fact]
    public async Task Session_link_hidden_when_session_not_publicly_viewable()
    {
        using var db = NewDb();
        var s = await SeedAsync(db, aliceSelected: true); // profile published...
        // ...but the session is NOT publicly viewable: turn it into a service
        // session (breaks/lunch are never publicly addressable — same gate as the
        // public page, which 404s for these).
        var aliceSession = await db.Sessions
            .FirstAsync(x => x.SessionizeId == "s-alice");
        aliceSession.IsServiceSession = true;
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = Session(s.Alice) };
        var model = NewModel(db, http);

        await model.OnGetAsync(default);

        // The session is not publicly viewable, so its id is NOT in the set
        // (the view then hides the link / shows the "not public yet" hint).
        Assert.DoesNotContain(aliceSession.Id, model.PubliclyViewableSessionIds);
    }
}
