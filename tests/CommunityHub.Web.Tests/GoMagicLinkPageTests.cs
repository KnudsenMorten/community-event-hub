using System.Security.Claims;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// The §169 /go magic-link resolver page (<see cref="Pages.GoModel"/>) over a fake
/// HttpContext + EF in-memory + the ephemeral DataProtection provider:
///   - a LIVE token signs the participant in and redirects to the intended page
///     (default "/", an explicit ?r=, or the trailing catch-all deep-link);
///   - a bad / expired / revoked token NEVER throws — it falls through to the
///     Login page, pre-staging the recovery email when the link was genuine;
///   - an external/protocol-relative return target is dropped.
/// FAKE names only.
/// </summary>
public sealed class GoMagicLinkPageTests
{
    private const int EventId = 9;
    private static readonly DateTimeOffset Now = new(2026, 6, 28, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"go-{Guid.NewGuid():N}")
            .Options);

    private static EmailMagicLinkService NewService(CommunityHubDbContext db, DateTimeOffset? now = null) =>
        new(db,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-dp-go-tests"))),
            new FixedClock(now ?? Now));

    private sealed class CapturingAuthService : IAuthenticationService
    {
        public ClaimsPrincipal? LastSignedIn { get; private set; }
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            LastSignedIn = principal;
            context.User = principal;
            return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private static (DefaultHttpContext http, CapturingAuthService auth) NewHttpContext()
    {
        var auth = new CapturingAuthService();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(auth);
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        return (http, auth);
    }

    private static Pages.GoModel NewModel(EmailMagicLinkService svc, CommunityHubDbContext db, DefaultHttpContext http) =>
        new(svc, db) { PageContext = new PageContext { HttpContext = http } };

    private static async Task<Participant> SeedAsync(CommunityHubDbContext db, bool isActive = true)
    {
        var p = new Participant
        {
            EventId = EventId,
            Email = "speaker@example.com",
            FullName = "Test Speaker",
            Role = ParticipantRole.Speaker,
            IsActive = isActive,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task A_live_token_signs_in_and_redirects_to_root_by_default()
    {
        using var db = NewDb();
        var p = await SeedAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(p.Id);
        var (http, auth) = NewHttpContext();

        var result = await NewModel(svc, db, http).OnGetAsync(token, r: null, target: null, default);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/", redirect.Url);
        Assert.NotNull(auth.LastSignedIn);
        Assert.Equal(p.Id.ToString(), auth.LastSignedIn!.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }

    [Fact]
    public async Task A_fresh_grant_authenticates_and_redirects_to_the_target_not_login()
    {
        // §190 end-to-end: a signed-OUT speaker clicks an email CTA that deep-links to
        // /Speaker through their /go/{token} magic-link. The Go page must SIGN THEM IN
        // and redirect to /Speaker — never bounce them to /Login.
        using var db = NewDb();
        var p = await SeedAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(p.Id);
        var (http, auth) = NewHttpContext();

        var result = await NewModel(svc, db, http).OnGetAsync(token, r: "/Speaker", target: null, default);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Speaker", redirect.Url);                 // lands on the intended target…
        Assert.NotEqual("/Login", redirect.Url);                // …NOT the login page
        Assert.NotNull(auth.LastSignedIn);                      // AUTHENTICATED principal established
        Assert.Equal(p.Id.ToString(),
            auth.LastSignedIn!.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }

    [Fact]
    public async Task A_live_token_honours_an_explicit_r_return_url()
    {
        using var db = NewDb();
        var p = await SeedAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(p.Id);
        var (http, _) = NewHttpContext();

        var result = await NewModel(svc, db, http).OnGetAsync(token, r: "/Tasks", target: null, default);

        Assert.Equal("/Tasks", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task A_live_token_honours_a_trailing_catch_all_deep_link()
    {
        using var db = NewDb();
        var p = await SeedAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(p.Id);
        var (http, _) = NewHttpContext();

        // {{hubUrl}}/Speaker/Graphics → /go/{token}/Speaker/Graphics → target="Speaker/Graphics".
        var result = await NewModel(svc, db, http).OnGetAsync(token, r: null, target: "Speaker/Graphics", default);

        Assert.Equal("/Speaker/Graphics", Assert.IsType<RedirectResult>(result).Url);
    }

    /// <summary>
    /// §365 — THE QUERY STRING MUST SURVIVE. A route catch-all captures the PATH only, so
    /// <c>{{hubUrl}}/Forms/Wizard?step=masterclass</c> used to arrive as target="Forms/Wizard" with
    /// the <c>?step=</c> silently dropped, landing the attendee on the wizard's FIRST step instead of
    /// the one the mail button promised. Nine master-class CTAs (plus the party equivalent) were
    /// affected — and §351-7 had re-pointed them at the wizard precisely to fix a bad landing.
    /// </summary>
    [Fact]
    public async Task A_deep_link_keeps_its_query_string_so_the_wizard_opens_the_promised_step()
    {
        using var db = NewDb();
        var p = await SeedAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(p.Id);
        var (http, _) = NewHttpContext();
        http.Request.QueryString = new QueryString("?step=masterclass");

        var result = await NewModel(svc, db, http).OnGetAsync(token, r: null, target: "Forms/Wizard", default);

        Assert.Equal("/Forms/Wizard?step=masterclass", Assert.IsType<RedirectResult>(result).Url);
    }

    /// <summary>
    /// <c>r</c> is the /go page's OWN parameter, not the destination's — re-appending it would make
    /// a stray <c>?r=</c> show up in the address bar of the page the recipient lands on.
    /// </summary>
    [Fact]
    public async Task The_r_parameter_is_not_re_appended_to_the_destination()
    {
        using var db = NewDb();
        var p = await SeedAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(p.Id);
        var (http, _) = NewHttpContext();
        http.Request.QueryString = new QueryString("?r=%2FTasks&step=party");

        var result = await NewModel(svc, db, http).OnGetAsync(token, r: null, target: "Forms/Wizard", default);

        Assert.Equal("/Forms/Wizard?step=party", Assert.IsType<RedirectResult>(result).Url);
    }

    /// <summary>
    /// The §234 local-only guarantee is unchanged by the query carry: appending a query string must
    /// not rescue a target <see cref="Pages.GoModel"/> would otherwise refuse. A backslash is the
    /// live vector — browsers read "/\evil.example.com" as protocol-relative.
    /// </summary>
    [Fact]
    public async Task A_query_string_cannot_rescue_a_target_the_local_guard_rejects()
    {
        using var db = NewDb();
        var p = await SeedAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(p.Id);
        var (http, _) = NewHttpContext();
        http.Request.QueryString = new QueryString("?step=masterclass");

        var result = await NewModel(svc, db, http).OnGetAsync(
            token, r: null, target: "\\evil.example.com/phish", default);

        Assert.Equal("/", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task A_protocol_relative_target_is_dropped_and_falls_back_to_root()
    {
        using var db = NewDb();
        var p = await SeedAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(p.Id);
        var (http, auth) = NewHttpContext();

        var result = await NewModel(svc, db, http).OnGetAsync(token, r: "//evil.example.com/phish", target: null, default);

        Assert.Equal("/", Assert.IsType<RedirectResult>(result).Url);  // not honoured
        Assert.NotNull(auth.LastSignedIn);                              // still a real sign-in
    }

    [Fact]
    public async Task A_backslash_target_is_dropped_and_falls_back_to_root()
    {
        // §234 4a: browsers treat "/\evil.com" as protocol-relative — a plain
        // "starts with / but not //" check is an open redirect. Any '\' is rejected.
        using var db = NewDb();
        var p = await SeedAsync(db);
        var svc = NewService(db);
        var token = await svc.GetOrCreateTokenAsync(p.Id);
        var (http, auth) = NewHttpContext();

        var result = await NewModel(svc, db, http).OnGetAsync(token, r: "/\\evil.example.com/phish", target: null, default);

        Assert.Equal("/", Assert.IsType<RedirectResult>(result).Url);  // not honoured
        Assert.NotNull(auth.LastSignedIn);                              // still a real sign-in
    }

    [Fact]
    public async Task An_unknown_token_never_throws_and_falls_through_to_login()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var svc = NewService(db);
        var (http, auth) = NewHttpContext();

        var result = await NewModel(svc, db, http).OnGetAsync("garbage-token", r: null, target: null, default);

        Assert.Equal("/Login", Assert.IsType<RedirectResult>(result).Url);
        Assert.Null(auth.LastSignedIn);   // never signed in
    }

    [Fact]
    public async Task An_expired_token_falls_through_to_login_prestaged_with_the_recovery_email()
    {
        using var db = NewDb();
        var p = await SeedAsync(db);
        var token = await NewService(db).GetOrCreateTokenAsync(p.Id);
        var (http, auth) = NewHttpContext();

        // Resolve a year-and-a-day later: the link has expired.
        var future = NewService(db, Now.AddDays(366));
        var result = await NewModel(future, db, http).OnGetAsync(token, r: "/Tasks", target: null, default);

        var url = Assert.IsType<RedirectResult>(result).Url;
        Assert.StartsWith("/Login?", url);
        Assert.Contains("email=speaker%40example.com", url);   // pre-staged
        Assert.Contains("ReturnUrl=%2FTasks", url);            // intended destination carried
        Assert.Null(auth.LastSignedIn);
    }
}
