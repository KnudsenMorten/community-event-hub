using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// FAIL-CLOSED authorization backstop contract (security hardening).
///
/// Program.cs installs an <see cref="AuthorizationOptions.FallbackPolicy"/> of
/// <c>RequireAuthenticatedUser()</c>, so any endpoint that does NOT carry its own
/// authorization metadata is private by default — a future page added without an
/// explicit attribute can never be silently public. This suite pins that contract
/// without a live server/SQL (Program.cs runs EF <c>Migrate()</c> at startup, so the
/// real host can't boot in-test): it asserts the fallback semantics, the per-endpoint
/// opt-outs, and the Program.cs wiring of the public surfaces (/, /health, /set-language).
///
/// Mapping to the requested HTTP behaviour:
///   * Anonymous + fallback policy => NOT authorized  => the auth pipeline 302s to /Login
///     (i.e. an unauthenticated GET to an organizer page is "not 200").
///   * [AllowAnonymous] endpoint    => authorization short-circuits => reachable anonymously
///     (i.e. a public page and /health are 200 anonymous).
/// </summary>
public sealed class AuthorizationFallbackTests
{
    private static readonly Assembly WebAssembly = typeof(CommunityHub.Pages.IndexModel).Assembly;

    // Every currently-anonymous surface. Each MUST keep [AllowAnonymous] so it stays
    // reachable without a cookie once the fail-closed FallbackPolicy is in force.
    public static readonly string[] AnonymousEndpoints =
    {
        // Public Razor pages
        "CommunityHub.Pages.IndexModel",                       // /  (public landing + health probe target)
        "CommunityHub.Pages.AgendaModel",                      // /Agenda
        "CommunityHub.Pages.Sessions.IndexModel",              // /Sessions
        "CommunityHub.Pages.Sessions.DetailModel",             // /Sessions/{id}
        "CommunityHub.Pages.Sessions.EvaluateModel",           // /Sessions/Evaluate (QR rating)
        "CommunityHub.Pages.Sessions.AskModel",                // /Sessions/Ask (QR question)
        "CommunityHub.Pages.Speakers.IndexModel",              // /Speakers
        "CommunityHub.Pages.Speakers.DetailModel",             // /Speakers/{id}
        "CommunityHub.Pages.Sponsors.IndexModel",              // /Sponsors
        "CommunityHub.Pages.MasterClass.IndexModel",           // /MasterClass
        "CommunityHub.Pages.MasterClassesPublicModel",         // /MasterClasses
        "CommunityHub.Pages.MasterClassPageModel",             // /MasterClassPage (logistics)
        "CommunityHub.Pages.MyMasterClassModel",               // /MyMasterClass (landing)
        "CommunityHub.Pages.PartyModel",                       // /Party (anon RSVP)
        "CommunityHub.Pages.Volunteer.SignupModel",            // /Volunteer/Signup
        "CommunityHub.Pages.Survey.IndexModel",                // /Survey
        "CommunityHub.Pages.Survey.ResultsModel",              // /Survey/Results
        "CommunityHub.Pages.AttendeeTelemetryModel",           // /attendee-telemetry
        "CommunityHub.Pages.LoginModel",                       // /Login (+ PIN request/verify handlers)
        "CommunityHub.Pages.Login.MagicModel",                 // /Login/Magic
        // Token-secured API controllers (the token IS the credential; no cookie)
        "CommunityHub.Api.PublicSessionCalendarController",    // /Sessions/{id}.ics
        "CommunityHub.Api.CalendarController",                 // /cal/{token}.ics, /calendar/{token}.ics
        "CommunityHub.Api.SecretaryController",                // /s/{token}
        "CommunityHub.Api.SponsorLeadsController",             // /api/v1/sponsors/{id}/leads.*
    };

    public static TheoryData<string> AnonymousEndpointData()
    {
        var data = new TheoryData<string>();
        foreach (var name in AnonymousEndpoints) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(AnonymousEndpointData))]
    public void Public_and_token_endpoints_stay_anonymous(string fullTypeName)
    {
        var type = WebAssembly.GetType(fullTypeName, throwOnError: true)!;
        Assert.True(
            HasAllowAnonymous(type),
            $"{fullTypeName} must carry [AllowAnonymous]; otherwise the fail-closed " +
            "FallbackPolicy makes it require auth and the public/token surface breaks.");
    }

    [Theory]
    // Representative protected pages: these MUST inherit the fail-closed backstop
    // (or an explicit [Authorize]) so an unauthenticated GET is redirected to /Login,
    // never served 200. AttendeesModel is the example named in the task.
    [InlineData("CommunityHub.Pages.Organizer.AttendeesModel")]
    [InlineData("CommunityHub.Pages.Organizer.ParticipantsModel")]
    public void Organizer_pages_are_not_anonymous(string fullTypeName)
    {
        var type = WebAssembly.GetType(fullTypeName, throwOnError: true)!;
        Assert.False(
            HasAllowAnonymous(type),
            $"{fullTypeName} must NOT be [AllowAnonymous] — it must stay behind the " +
            "fail-closed backstop so anonymous requests are redirected to login.");
    }

    [Fact]
    public async Task Fallback_policy_denies_anonymous_but_allows_authenticated()
    {
        // The exact policy Program.cs installs as AuthorizationOptions.FallbackPolicy.
        var fallback = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        var authz = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();

        // No authentication type => IsAuthenticated == false.
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        // A non-null authentication type => IsAuthenticated == true (a signed-in cookie user).
        var signedIn = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "p1") }, "TestCookie"));

        // Anonymous on a fallback-protected endpoint => denied => pipeline 302s to /Login (not 200).
        Assert.False((await authz.AuthorizeAsync(anonymous, resource: null, fallback)).Succeeded);
        // Signed in => allowed => the page renders (200).
        Assert.True((await authz.AuthorizeAsync(signedIn, resource: null, fallback)).Succeeded);
    }

    [Fact]
    public void Program_wires_failclosed_fallback_and_keeps_health_anonymous()
    {
        var program = File.ReadAllText(Path.Combine(WebSrcDir(), "Program.cs"));

        // Fail-closed backstop is actually installed app-wide.
        Assert.Contains("FallbackPolicy", program);
        Assert.Contains("new AuthorizationPolicyBuilder()", program);
        Assert.Contains(".RequireAuthenticatedUser()", program);

        // The deploy health probe + the anonymous language switcher are explicitly opted out.
        Assert.Contains("MapHealthChecks(\"/health\").AllowAnonymous()", program);
        Assert.Contains(".AllowAnonymous();", program); // /set-language minimal API opt-out
    }

    private static bool HasAllowAnonymous(Type type) =>
        type.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any();

    private static string WebSrcDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub");
            if (File.Exists(Path.Combine(candidate, "Program.cs"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/CommunityHub/Program.cs from " + AppContext.BaseDirectory);
    }
}
