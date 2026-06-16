using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// The "Switch user" defect-and-contract test. The reported bug was that the
/// organizer's "Switch user" action landed on the limited 2-field
/// <c>/Organizer/EditOnBehalf</c> form instead of a REAL impersonation. This
/// suite drives the actual page-model handlers over a fake <see cref="HttpContext"/>
/// (a capturing <see cref="IAuthenticationService"/> stands in for the cookie
/// handler) and asserts the full industry-standard round-trip:
///
///   1. Switch user re-issues the session AS the target (target identity +
///      acting-as markers) and lands on the target's OWN hub root ("/"), NOT
///      the EditOnBehalf form.
///   2. The start is written to the ImpersonationAudit trail.
///   3. An already-acting session can never start a nested impersonation.
///   4. "Return to organizer" restores the organizer's own (un-marked) session
///      and is audited.
/// </summary>
public sealed class SwitchUserImpersonationTests
{
    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"switchuser-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-15T10:00:00Z");
    }

    /// <summary>
    /// Captures SignIn/SignOut and reflects the last signed-in principal back
    /// onto the HttpContext so a subsequent handler observes the new session —
    /// exactly as the cookie handler would across requests.
    /// </summary>
    private sealed class CapturingAuthService : IAuthenticationService
    {
        public ClaimsPrincipal? LastSignedIn { get; private set; }
        public bool SignedOut { get; private set; }

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            LastSignedIn = principal;
            context.User = principal; // make the new session visible to the next handler
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOut = true;
            return Task.CompletedTask;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    /// <summary>Build an HttpContext whose RequestServices can do SignInAsync.</summary>
    private static (DefaultHttpContext http, CapturingAuthService auth) NewHttpContext()
    {
        var auth = new CapturingAuthService();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(auth);
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        return (http, auth);
    }

    private static ClaimsPrincipal NormalSession(Participant p)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new(ClaimTypes.Email, p.Email),
            new(ClaimTypes.Name, p.FullName),
            new(ClaimTypes.Role, p.Role.ToString()),
            new("EventId", p.EventId.ToString()),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static ParticipantsModel NewParticipantsModel(
        CommunityHubDbContext db, DefaultHttpContext http, TimeProvider clock)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        var model = new ParticipantsModel(
            db, accessor, new ParticipantBulkOperationService(db),
            new ParticipantDeletionService(db, clock),
            new ParticipantSearchService(db),
            new ImpersonationAuditService(db, clock), clock)
        {
            PageContext = new PageContext { HttpContext = http },
        };
        return model;
    }

    private static ReturnToOrganizerModel NewReturnModel(
        CommunityHubDbContext db, DefaultHttpContext http, TimeProvider clock)
    {
        var accessor = new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));
        return new ReturnToOrganizerModel(
            accessor, db, new ImpersonationAuditService(db, clock), clock)
        {
            PageContext = new PageContext { HttpContext = http },
        };
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static async Task<(Participant organizer, Participant target)> SeedAsync(CommunityHubDbContext db)
    {
        var organizer = new Participant
        {
            EventId = EventId, Email = "org@example.test", FullName = "Olive Organizer",
            Role = ParticipantRole.Organizer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        var target = new Participant
        {
            EventId = EventId, Email = "spk@example.test", FullName = "Sam Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.AddRange(organizer, target);
        await db.SaveChangesAsync();
        return (organizer, target);
    }

    // ---------------------------------------------------------------------

    [Fact]
    public async Task SwitchUser_reissues_session_as_target_and_lands_on_their_hub_not_EditOnBehalf()
    {
        using var db = NewDb();
        var clock = new FixedClock();
        var (organizer, target) = await SeedAsync(db);

        var (http, auth) = NewHttpContext();
        http.User = NormalSession(organizer);
        var model = NewParticipantsModel(db, http, clock);

        var result = await model.OnPostSwitchToUserAsync(target.Id, CancellationToken.None);

        // Lands on the target's own hub root — NOT the 2-field EditOnBehalf form.
        var redirect = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/", redirect.Url);
        Assert.Equal("/", ParticipantsModel.SwitchToUserLandingPath);
        Assert.DoesNotContain("EditOnBehalf", redirect.Url, StringComparison.OrdinalIgnoreCase);

        // The session was re-issued AS the target, carrying acting-as markers.
        Assert.NotNull(auth.LastSignedIn);
        var newSession = CurrentParticipant.FromPrincipal(auth.LastSignedIn);
        Assert.NotNull(newSession);
        Assert.Equal(target.Id, newSession!.ParticipantId);          // identity is the target
        Assert.Equal(target.Role, newSession.Role);
        Assert.True(newSession.IsActingAs);                          // ...but it's an acting-as session
        Assert.Equal(ImpersonationActorKind.Organizer, newSession.ActingAs!.Kind);
        Assert.Equal(organizer.Id, newSession.ActingAs.ActorParticipantId);

        // The start is audited.
        var audit = await db.ImpersonationAudits.SingleAsync();
        Assert.Equal(ImpersonationAuditService.ActionStart, audit.Action);
        Assert.Equal(organizer.Id, audit.ActorParticipantId);
        Assert.Equal(target.Id, audit.TargetParticipantId);
    }

    [Fact]
    public async Task An_acting_as_session_cannot_start_a_nested_impersonation()
    {
        using var db = NewDb();
        var clock = new FixedClock();
        var (organizer, target) = await SeedAsync(db);

        // First switch organizer -> target, so the session is now acting-as.
        var (http, auth) = NewHttpContext();
        http.User = NormalSession(organizer);
        await NewParticipantsModel(db, http, clock)
            .OnPostSwitchToUserAsync(target.Id, CancellationToken.None);

        // The acting-as session (now Sam Speaker, ActingAs) tries to switch again.
        var nested = await NewParticipantsModel(db, http, clock)
            .OnPostSwitchToUserAsync(organizer.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(nested);
        // No second "start" was written.
        Assert.Equal(1, await db.ImpersonationAudits.CountAsync(a => a.Action == ImpersonationAuditService.ActionStart));
    }

    [Fact]
    public async Task ReturnToOrganizer_restores_the_unmarked_organizer_session_and_audits()
    {
        using var db = NewDb();
        var clock = new FixedClock();
        var (organizer, target) = await SeedAsync(db);

        var (http, auth) = NewHttpContext();
        http.User = NormalSession(organizer);
        await NewParticipantsModel(db, http, clock)
            .OnPostSwitchToUserAsync(target.Id, CancellationToken.None);
        Assert.True(CurrentParticipant.FromPrincipal(http.User)!.IsActingAs);

        // Now return.
        var result = await NewReturnModel(db, http, clock).OnPostAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Organizer/Participants", redirect.PageName);

        // Session is the organizer again, with NO acting-as markers.
        var restored = CurrentParticipant.FromPrincipal(http.User);
        Assert.NotNull(restored);
        Assert.Equal(organizer.Id, restored!.ParticipantId);
        Assert.False(restored.IsActingAs);

        // Both the start and the return are on the trail.
        Assert.Equal(1, await db.ImpersonationAudits.CountAsync(a => a.Action == ImpersonationAuditService.ActionStart));
        Assert.Equal(1, await db.ImpersonationAudits.CountAsync(a => a.Action == ImpersonationAuditService.ActionReturn));
    }

    [Fact]
    public async Task Switching_to_yourself_is_rejected_without_starting_a_session()
    {
        using var db = NewDb();
        var clock = new FixedClock();
        var (organizer, _) = await SeedAsync(db);

        var (http, auth) = NewHttpContext();
        http.User = NormalSession(organizer);

        var result = await NewParticipantsModel(db, http, clock)
            .OnPostSwitchToUserAsync(organizer.Id, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(auth.LastSignedIn);
        Assert.Equal(0, await db.ImpersonationAudits.CountAsync());
    }
}
