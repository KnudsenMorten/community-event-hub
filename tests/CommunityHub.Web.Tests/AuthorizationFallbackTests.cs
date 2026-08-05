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
        // §748 C6 — /f/{token}, the page a session's printed QR code opens. 🔒 Anonymous BY DESIGN:
        // the token is the only authorisation, and the brief forbids collecting identity here.
        // A login redirect would make every printed code in the venue useless.
        "CommunityHub.Pages.FeedbackModel",                    // /f/{token}
        "CommunityHub.Pages.Sessions.AskModel",                // /Sessions/Ask (QR question)
        "CommunityHub.Pages.Speakers.IndexModel",              // /Speakers
        "CommunityHub.Pages.Speakers.DetailModel",             // /Speakers/{id}
        "CommunityHub.Pages.Sponsors.IndexModel",              // /Sponsors
        "CommunityHub.Pages.MasterClass.IndexModel",           // /MasterClass
        "CommunityHub.Pages.MasterClassesPublicModel",         // /MasterClasses
        "CommunityHub.Pages.MasterClassPageModel",             // /MasterClassPage (logistics)
        "CommunityHub.Pages.MyMasterClassModel",               // /MyMasterClass (landing)
        // §206: /Party is now AUTHENTICATED-only (no anonymous RSVP) — see
        // Protected_pages_are_not_anonymous below.
        "CommunityHub.Pages.Volunteer.SignupModel",            // /Volunteer/Signup
        "CommunityHub.Pages.Survey.IndexModel",                // /Survey
        "CommunityHub.Pages.Survey.ResultsModel",              // /Survey/Results
        "CommunityHub.Pages.AttendeeTelemetryModel",           // /attendee-telemetry
        "CommunityHub.Pages.LoginModel",                       // /Login (+ PIN request/verify handlers)
        "CommunityHub.Pages.Login.MagicModel",                 // /Login/Magic
        // Token-secured API controllers (the token IS the credential; no cookie)
        "CommunityHub.Api.SecretaryController",                // /s/{token}
        "CommunityHub.Api.SponsorLeadsController",             // /api/v1/sponsors/{id}/leads.*
        // §743 C4a — the feedback devices. Their credential is a per-device pre-shared key in a
        // header, not a cookie. 🔒 Without [AllowAnonymous] the fail-closed fallback would redirect
        // every upload to /Login — and these units have no browser, no screen and no way to report
        // why they had stopped working. The failure would look like dead hardware.
        "CommunityHub.Api.EvaluationIngestController",         // /evaluation/v1/ingest/*
        // §747 C8 — the outbound report pull. Its credential is a service key in a header, not a
        // cookie. 🔒 Without [AllowAnonymous] the fail-closed fallback would redirect a machine
        // consumer to /Login, which answers 200 with an HTML page — the worst failure mode there is,
        // because the client cannot tell it from success and would archive a login page as a report.
        "CommunityHub.Api.EvaluationReportController",         // /evaluation/v1/events/*/sessions/*/report
        // 🔴 §753 C4b — device onboarding. THE ONLY ENDPOINT THAT ACCEPTS AN UNAUTHENTICATED WRITE,
        // and anonymous by necessity: a virgin unit has no device key yet, which is the whole point
        // of it. What guards it is a fleet-wide bootstrap secret in a header, plus the fact that
        // reaching it grants NOTHING — a caller gets a row in a queue and a human decides the rest.
        // 🔒 It fails CLOSED when the secret is unconfigured (503), never open.
        "CommunityHub.Api.EvaluationProvisionController",      // /evaluation/v1/provision
        // ---- Found by No_endpoint_is_anonymous_unless_it_is_on_the_declared_list, 2026-08-01 ----
        // 🔑 These five were ALREADY anonymous and already deliberate — each says so in its own
        // source — but none had ever been declared here, so the list was not the public surface it
        // claimed to be. Declared now, with the reason, so the count is honest.
        "CommunityHub.Pages.AboutModel",                       // /About — public information
        "CommunityHub.Pages.ContributorsModel",                // /Contributors — public credits
        // §169 — the magic-link resolver. 🔒 Anonymous BY NECESSITY: the token in the URL IS the
        // credential, and requiring a cookie first would break every e-mail button we send.
        "CommunityHub.Pages.GoModel",                          // /go/{token}/{**target}
        // §322c/§322e — the public slides catalogue and embedded viewer. Attendees need no login and
        // never see a SharePoint URL; the hub proxies with the app registration's credentials.
        "CommunityHub.Pages.Sessions.SlidesModel",             // /Sessions/Slides
        "CommunityHub.Pages.Sessions.SlideViewModel",          // /Sessions/Slides/{id}/{kind}
        // §754 — the venue signage screens, /signage/{orientation}/{view}. 🔒 Anonymous BY
        // NECESSITY: an OptiSigns player has no session, no keyboard and no way to complete a
        // login. The unguessable ?t= token is the entire credential, exactly as for /f/{token}.
        // What makes that safe is that the page is strictly READ-ONLY and shows only what is
        // already public in a corridor — an agenda and an aggregate score. 🔒 It also never
        // returns an error status: every refusal renders a holding screen at 200, because a
        // player displays whatever came back and an error page IS the failure, two metres tall.
        "CommunityHub.Pages.Signage.ScreenModel",              // /signage/{orientation}/{view}
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
    [InlineData("CommunityHub.Pages.PartyModel")]   // §206: authenticated-only party signup
    // §743 C1 — the device board shows identifiers, locations and health. The INGEST endpoints are
    // anonymous by necessity; this page must never follow them out.
    [InlineData("CommunityHub.Pages.Organizer.EvaluationDevicesModel")]
    [InlineData("CommunityHub.Pages.Organizer.EvaluationSessionsModel")]
    // §743 C7 — results carry per-session scores; organizer-only until C9's public scoreboard,
    // which is a DIFFERENT page with aggregate figures and no free text.
    [InlineData("CommunityHub.Pages.Organizer.EvaluationResultsModel")]
    // §747 C8 — this page ISSUES the service keys. The report controller is anonymous by necessity;
    // the page that mints its credentials must never follow it out.
    [InlineData("CommunityHub.Pages.Organizer.EvaluationApiClientsModel")]
    // §748 C5 — the QR page. The FEEDBACK page it points at is anonymous by design; the page that
    // mints and hands out the tokens must never follow it out.
    [InlineData("CommunityHub.Pages.Organizer.SessionQrCodesModel")]
    // 🔴 §753 C4b — the onboarding queue. The provisioning ENDPOINT is anonymous by necessity; the
    // page that APPROVES what it queues is the entire control on it and must never follow it out.
    // An anonymous approval screen would turn self-request straight back into self-registration.
    [InlineData("CommunityHub.Pages.Organizer.EvaluationOnboardingModel")]
    // 🔴 §754 — the signage control panel. The SCREENS are anonymous by necessity; this is the page
    // that MINTS and rotates their tokens and prints the complete URLs. Same rule as §753's
    // onboarding queue: an endpoint may be public, the page that issues its credentials never is.
    [InlineData("CommunityHub.Pages.Organizer.SignageModel")]
    public void Organizer_pages_are_not_anonymous(string fullTypeName)
    {
        var type = WebAssembly.GetType(fullTypeName, throwOnError: true)!;
        Assert.False(
            HasAllowAnonymous(type),
            $"{fullTypeName} must NOT be [AllowAnonymous] — it must stay behind the " +
            "fail-closed backstop so anonymous requests are redirected to login.");
    }

    /// <summary>
    /// 🔴 §753 — the INVERSE of the list above: nothing may become anonymous without being declared.
    /// </summary>
    /// <remarks>
    /// <para><b>This suite had a hole and adding an anonymous endpoint found it.</b> Every check here
    /// asked "are the endpoints we EXPECT to be public still public?" and "are these named organizer
    /// pages still private?" — so a brand-new <c>[AllowAnonymous]</c> anywhere else passed in
    /// silence. I added <c>EvaluationProvisionController</c>, an endpoint that accepts an
    /// unauthenticated WRITE, and the whole suite stayed green.</para>
    ///
    /// <para>🔒 The fail-closed <c>FallbackPolicy</c> protects against forgetting to add
    /// <c>[Authorize]</c>. Nothing protected against DELIBERATELY writing <c>[AllowAnonymous]</c>,
    /// which is the more dangerous act because it is intentional and looks considered in review.
    /// Now the public surface can only grow by editing the list above — which puts a reviewer in
    /// front of the decision and asks them to write down why.</para>
    /// </remarks>
    [Fact]
    public void No_endpoint_is_anonymous_unless_it_is_on_the_declared_list()
    {
        var declared = new HashSet<string>(AnonymousEndpoints, StringComparer.Ordinal);

        var undeclared = WebAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t)
                        || typeof(Microsoft.AspNetCore.Mvc.RazorPages.PageModel).IsAssignableFrom(t))
            .Where(HasAllowAnonymous)
            .Select(t => t.FullName!)
            .Where(n => !declared.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "These carry [AllowAnonymous] but are not declared in AnonymousEndpoints:\n  "
            + string.Join("\n  ", undeclared)
            + "\n\nIf a new public surface is intended, ADD IT TO THAT LIST with a comment saying "
            + "why it must be reachable without a cookie. If it is not intended, remove the "
            + "attribute — an undeclared anonymous endpoint is how a private page becomes public "
            + "without anyone deciding to make it so.");
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
