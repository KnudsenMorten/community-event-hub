using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Resources;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Server-side authorization-gate contract for the sponsor self-service pages and
/// the organizer session QR-download handler (mirrors the role-gate the sibling
/// sponsor pages — Portal/CaptureLead/Leads — already enforce, and the organizer
/// <c>Guard()</c> the session page's mutating handlers use). Drives the real page
/// models over a fake <see cref="HttpContext"/>. Proves:
///  - a non-sponsor role is access-denied (friendly branch, not the content) on
///    <c>/Sponsor/Index</c>, while a sponsor is let through,
///  - the <c>OnGetDownloadQrAsync</c> handler is organizer-only: a non-organizer
///    is Forbidden, an organizer passes the gate.
/// FAKE names only.
/// </summary>
public sealed class SponsorOrganizerAuthzGateTests
{
    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"authz-gate-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-18T10:00:00Z");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static DefaultHttpContext ContextFor(ParticipantRole role, int participantId = 1)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, participantId.ToString()),
            new(ClaimTypes.Email, "person@example.test"),
            new(ClaimTypes.Name, "Test Person"),
            new(ClaimTypes.Role, role.ToString()),
            new("EventId", EventId.ToString()),
        };
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
        };
    }

    private static IStringLocalizer<SharedResource> Loc()
    {
        var options = Options.Create(new LocalizationOptions { ResourcesPath = "" });
        var factory = new ResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    // --- /Sponsor/Index role gate --------------------------------------------
    // The constructor takes several integration services, but the role gate fires
    // at the top of OnGetAsync and returns before any of them is touched, so the
    // unused services are passed as null (same convention as the session/leads
    // page-model tests).
    private static CommunityHub.Pages.Sponsor.IndexModel NewSponsorIndex(
        CommunityHubDbContext db, DefaultHttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new CommunityHub.Pages.Sponsor.IndexModel(
            db, accessor,
            cm: null!, cmOptions: new CommunityHub.Core.Integrations.CompanyManagerOptions(),
            woo: null!, wooOptions: new CommunityHub.Core.Integrations.WooCommerceOptions(),
            cache: new MemoryCache(new MemoryCacheOptions()),
            eventConfigLoader: new EventEditionConfigLoader(),
            eventConfigOptions: new EventConfigOptions(),
            log: NullLogger<CommunityHub.Pages.Sponsor.IndexModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    [Fact]
    public async Task SponsorIndex_non_sponsor_role_is_access_denied()
    {
        using var db = NewDb();
        var http = ContextFor(ParticipantRole.Attendee);
        var model = NewSponsorIndex(db, http);

        var result = await model.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
        // The gate short-circuits before any company / order lookup.
        Assert.False(model.NoCompanyLink);
        Assert.Null(model.CompanyDetails);
    }

    [Fact]
    public async Task SponsorIndex_sponsor_role_passes_the_gate()
    {
        using var db = NewDb();
        var http = ContextFor(ParticipantRole.Sponsor);
        var model = NewSponsorIndex(db, http);

        var result = await model.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);
        // No SponsorCompanyId linked for this participant ⇒ the friendly
        // not-linked branch, NOT the role denial.
        Assert.True(model.NoCompanyLink);
    }

    // --- Organizer Sessions QR-download handler organizer gate ----------------
    private static SessionsModel NewSessions(CommunityHubDbContext db, DefaultHttpContext http)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new SessionsModel(db, accessor, null!, null!, null!, null!, null!, null!, null!,
            new FixedClock(), Loc(), new CommunityHub.Core.Settings.FeatureGateService(db))
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private static async Task<int> SeedSessionWithQrAsync(CommunityHubDbContext db)
    {
        var s = new Session
        {
            EventId = EventId, Title = "Talk with QR", Room = "Room A",
            Type = SessionType.TechnicalSession, Length = SessionLength.SixtyMin,
            SessionizeId = Guid.NewGuid().ToString("N"),
            RoomQrUrl = "https://contoso.example/qr/room-a.png",
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    [Fact]
    public async Task DownloadQr_non_organizer_is_forbidden()
    {
        using var db = NewDb();
        var id = await SeedSessionWithQrAsync(db);
        // A signed-in NON-organizer (e.g. an attendee) must not be able to call
        // the handler even though a QR is provisioned.
        var http = ContextFor(ParticipantRole.Attendee);
        var model = NewSessions(db, http);

        var result = await model.OnGetDownloadQrAsync(id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task DownloadQr_organizer_passes_the_gate()
    {
        using var db = NewDb();
        var id = await SeedSessionWithQrAsync(db);
        var http = ContextFor(ParticipantRole.Organizer);
        var model = NewSessions(db, http);

        var result = await model.OnGetDownloadQrAsync(id, CancellationToken.None);

        // Past the gate: the provisioned QR redirects to the stored image
        // (never a Forbid for the organizer).
        Assert.IsNotType<ForbidResult>(result);
        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("https://contoso.example/qr/room-a.png", redirect.Url);
    }

    [Fact]
    public async Task DownloadQr_anonymous_is_redirected_to_login()
    {
        using var db = NewDb();
        var id = await SeedSessionWithQrAsync(db);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var model = NewSessions(db, http);

        var result = await model.OnGetDownloadQrAsync(id, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Login", redirect.PageName);
    }
}
