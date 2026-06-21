using System.Security.Claims;
using CommunityHub.Auth;
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
/// The magic-link landing recovery states (REQUIREMENTS §21 Participant "Login
/// recovery states"). Drives the real <see cref="Pages.Login.MagicModel"/> over
/// a fake HttpContext + EF in-memory + the ephemeral DataProtection provider:
///   - a LIVE token signs the participant in and redirects to the intended page;
///   - an EXPIRED-but-genuine token is NOT a dead end — it shows the invalid
///     error key, recovers the recipient's email, and the recovery link carries
///     the email + the intended return URL into the email + PIN flow;
///   - a deactivated account is reported with its own key (no false pre-fill);
///   - a tampered/alien token shows the error with a blank (bare /Login) recovery.
/// FAKE names only.
/// </summary>
public sealed class MagicLinkRecoveryPageTests
{
    private const int EventId = 9;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"magic-{Guid.NewGuid():N}")
            .Options);

    private static MagicLinkService NewMagic() =>
        new(DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-dp-magic-tests"))));

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

    // A real single-use welcome auto-login service over the same db. These tests
    // present REUSABLE invitation tokens (MagicLinkService), which use a different
    // DataProtection purpose, so this service simply doesn't redeem them and the
    // page falls through to the legacy invitation-token path under test.
    private static CommunityHub.Core.Auth.IWelcomeAutoLoginTokenService NewWelcomeAutoLogin(
        CommunityHubDbContext db) =>
        new CommunityHub.Core.Auth.WelcomeAutoLoginTokenService(
            db,
            DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-dp-autologin-tests"))),
            TimeProvider.System);

    private static Pages.Login.MagicModel NewModel(
        MagicLinkService magic, CommunityHubDbContext db, DefaultHttpContext http) =>
        new(magic, NewWelcomeAutoLogin(db), db)
        { PageContext = new PageContext { HttpContext = http } };

    private static async Task<Participant> SeedAsync(
        CommunityHubDbContext db, bool isActive = true)
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
    public async Task A_live_token_signs_in_and_redirects_to_the_intended_page()
    {
        using var db = NewDb();
        var p = await SeedAsync(db);
        var magic = NewMagic();
        var token = magic.CreateToken(p.Id, TimeSpan.FromMinutes(10));
        var (http, auth) = NewHttpContext();
        var model = NewModel(magic, db, http);

        var result = await model.OnGetAsync(token, "/Forms/Hotel", default);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/Forms/Hotel", redirect.Url);
        Assert.NotNull(auth.LastSignedIn);
        Assert.Null(model.ErrorKey);
    }

    [Fact]
    public async Task An_expired_token_is_not_a_dead_end_it_recovers_the_email_and_return_url()
    {
        using var db = NewDb();
        var p = await SeedAsync(db);
        var magic = NewMagic();
        var expired = magic.CreateToken(p.Id, TimeSpan.FromMinutes(-1));
        var (http, _) = NewHttpContext();
        var model = NewModel(magic, db, http);

        var result = await model.OnGetAsync(expired, "/Speaker", default);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Login.MagicInvalid", model.ErrorKey);
        Assert.Equal("speaker@example.com", model.RecoveryEmail);
        Assert.Equal("/Speaker", model.ReturnUrl);

        // The recovery link drops the participant on the PIN flow pre-staged.
        var link = model.RecoveryLink();
        Assert.StartsWith("/Login?", link);
        Assert.Contains("email=speaker%40example.com", link);
        Assert.Contains("ReturnUrl=%2FSpeaker", link);
    }

    [Fact]
    public async Task A_deactivated_account_is_reported_without_a_false_prefill()
    {
        using var db = NewDb();
        var p = await SeedAsync(db, isActive: false);
        var magic = NewMagic();
        var token = magic.CreateToken(p.Id, TimeSpan.FromMinutes(10));
        var (http, auth) = NewHttpContext();
        var model = NewModel(magic, db, http);

        var result = await model.OnGetAsync(token, null, default);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Login.MagicInactive", model.ErrorKey);
        Assert.Null(auth.LastSignedIn);          // never signed in
        Assert.Null(model.RecoveryEmail);        // no "this will work" implication
        Assert.Equal("/Login", model.RecoveryLink());
    }

    [Fact]
    public async Task A_tampered_or_missing_token_shows_the_error_with_a_bare_recovery_link()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var magic = NewMagic();
        var (http, _) = NewHttpContext();
        var model = NewModel(magic, db, http);

        var result = await model.OnGetAsync("garbage-token", null, default);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Login.MagicInvalid", model.ErrorKey);
        Assert.Null(model.RecoveryEmail);
        Assert.Equal("/Login", model.RecoveryLink());
    }

    [Fact]
    public async Task A_cross_site_return_url_is_dropped_from_the_recovery_link()
    {
        using var db = NewDb();
        var p = await SeedAsync(db);
        var magic = NewMagic();
        var expired = magic.CreateToken(p.Id, TimeSpan.FromMinutes(-1));
        var (http, _) = NewHttpContext();
        var model = NewModel(magic, db, http);

        await model.OnGetAsync(expired, "//evil.example.com/phish", default);

        Assert.Null(model.ReturnUrl);                       // not honoured
        Assert.DoesNotContain("evil.example.com", model.RecoveryLink());
    }
}
