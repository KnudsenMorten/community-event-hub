using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Pages.Organizer;
using CommunityHub.Telemetry;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §392 — the organizer <c>/Organizer/PlatformHealth</c> page (operator 2026-07-26: <i>"can you also
/// add telemetry view graphs + stats on the admin interface"</i> → <i>"go build the telemetry
/// page"</i>).
///
/// <para>What is worth pinning here is NOT the KQL — that only proves itself against a live
/// workspace. It is the two things that decide whether the page can be trusted at all: that it
/// applies the real-organizer gate (this is platform-wide operational data, so an acting-as session
/// must not see it), and that an UNCONFIGURED or FAILING telemetry backend produces an explanation
/// instead of an exception. A diagnostics page that 500s when the platform is unhealthy is exactly
/// backwards, and that is precisely when someone opens it.</para>
/// </summary>
public sealed class PlatformHealthPageTests
{
    private const int EventId = 7;

    // ---- gate ------------------------------------------------------------

    [Fact]
    public async Task A_real_organizer_gets_the_page()
    {
        var page = Page(RealOrganizer(), configured: false);

        var result = await page.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(page.AccessDenied);
    }

    [Fact]
    public async Task A_non_organizer_is_refused()
    {
        var page = Page(Session(ParticipantRole.Speaker), configured: false);

        await page.OnGetAsync(default);

        Assert.True(page.AccessDenied);
        Assert.Null(page.Data);   // and no query is even attempted
    }

    [Fact]
    public async Task An_ACTING_AS_session_is_refused_even_though_its_role_claim_says_Organizer()
    {
        // The acting-as session carries the TARGET's claims, Role included — so a role-only check
        // passes. This page shows platform-wide operational data that has nothing to do with the
        // person being viewed, so there is no reading under which acting-as should see it.
        var page = Page(ActingAsIntoOrganizer(), configured: false);

        await page.OnGetAsync(default);

        Assert.True(page.AccessDenied);
    }

    [Fact]
    public async Task A_signed_out_visitor_is_sent_to_login_not_shown_a_denial()
    {
        var page = Page(new DefaultHttpContext(), configured: false);

        var result = await page.OnGetAsync(default);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Login", redirect.PageName);
    }

    // ---- fail-soft -------------------------------------------------------

    [Fact]
    public async Task An_UNCONFIGURED_environment_explains_itself_instead_of_throwing()
    {
        var page = Page(RealOrganizer(), configured: false);

        await page.OnGetAsync(default);

        Assert.False(page.IsConfigured);
        Assert.NotNull(page.Data);
        Assert.False(page.Data!.Ok);
        Assert.Contains("not configured", page.Data.Message, StringComparison.OrdinalIgnoreCase);
        // It must name the setting — "not configured" with no next step is a dead end.
        Assert.Contains("Telemetry:AppInsightsResourceId", page.Data.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configured_environment_that_cannot_REACH_telemetry_still_renders()
    {
        // A bogus resource id is the closest stand-in for the real failure modes (role assignment
        // not propagated, wrong id, throttling): the query is attempted and fails. The page must
        // come back with a message, not an exception.
        var page = Page(RealOrganizer(), configured: true);

        var result = await page.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(page.Data);
        Assert.False(page.Data!.Ok);
        Assert.False(string.IsNullOrWhiteSpace(page.Data.Message));
    }

    // ---- presentation helpers, which the chart divides by ----------------

    [Fact]
    public void The_chart_scale_is_never_zero_so_an_empty_window_cannot_divide_by_it()
    {
        var page = Page(RealOrganizer(), configured: false);

        // No data loaded at all — the view still evaluates these while rendering.
        Assert.True(page.PeakRequests >= 1);
        Assert.True(page.PeakP95 >= 1);
        Assert.Equal(0, page.FailureRatePercent);
    }

    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1000, "1.0 s")]
    [InlineData(4500, "4.5 s")]
    public void A_duration_reads_the_way_someone_reporting_a_hang_thinks_about_it(double ms, string expected)
    {
        Assert.Equal(expected, PlatformHealthModel.Ms(ms));
    }

    [Theory]
    [InlineData(200, "#15803d")]     // fine
    [InlineData(1500, "#b45309")]    // noticeable
    [InlineData(5000, "#b91c1c")]    // the shape of a "it hangs" report
    public void Latency_colour_is_blunt_on_purpose(double ms, string colour)
    {
        Assert.Equal(colour, PlatformHealthModel.LatencyColour(ms));
    }

    // ---- harness ---------------------------------------------------------

    private static PlatformHealthModel Page(HttpContext http, bool configured)
    {
        var settings = new Dictionary<string, string?>();
        if (configured)
        {
            // Well-formed but not a real component — reachable code path, unreachable resource.
            settings["Telemetry:AppInsightsResourceId"] =
                "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-none"
                + "/providers/microsoft.insights/components/none";
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var svc = new PlatformTelemetryService(config, NullLogger<PlatformTelemetryService>.Instance);

        return new PlatformHealthModel(
            new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)), svc)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static DefaultHttpContext Session(ParticipantRole role, int participantId = 1) =>
        ContextOf(
        [
            new Claim(ClaimTypes.NameIdentifier, participantId.ToString()),
            new Claim(ClaimTypes.Email, "someone@example.test"),
            new Claim(ClaimTypes.Name, "Someone"),
            new Claim(ClaimTypes.Role, role.ToString()),
            new Claim("EventId", EventId.ToString()),
        ]);

    private static DefaultHttpContext RealOrganizer() => Session(ParticipantRole.Organizer);

    private static DefaultHttpContext ActingAsIntoOrganizer() => ContextOf(
    [
        new Claim(ClaimTypes.NameIdentifier, "99"),
        new Claim(ClaimTypes.Email, "target.organizer@example.test"),
        new Claim(ClaimTypes.Name, "Target Organizer"),
        new Claim(ClaimTypes.Role, ParticipantRole.Organizer.ToString()),
        new Claim("EventId", EventId.ToString()),
        new Claim(CommunityHub.Core.Auth.ActingAsClaims.ActorKind, ImpersonationActorKind.Organizer.ToString()),
        new Claim(CommunityHub.Core.Auth.ActingAsClaims.ActorParticipantId, "1"),
        new Claim(CommunityHub.Core.Auth.ActingAsClaims.ActorLabel, "Real Organizer"),
    ]);

    private static DefaultHttpContext ContextOf(List<Claim> claims) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
    };
}
