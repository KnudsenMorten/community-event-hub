using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Resources;
using CommunityHub.Core.Settings;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Acting-as / secretary write-authorization contract for the organizer pages.
///
/// An acting-as session (organizer "switch to user", or a secretary-token
/// session) deliberately carries the TARGET participant's identity claims,
/// INCLUDING the Role claim — so an acting-as session into another organizer
/// satisfies a plain <c>me.Role == Organizer</c> check while
/// <c>me.IsActingAs == true</c>. The defect: organizer pages that gated only on
/// the role let such a session perform organizer WRITES (mass email, Settings
/// kill-switches, EditParticipant role/email escalation, MarkPaid, deletes, …),
/// AND the audit trail mis-attributed those writes to the impersonated target.
///
/// The fix gates every state-changing organizer handler on
/// <see cref="OrganizerAuth.IsRealOrganizer"/> (role Organizer AND not acting-as).
/// This suite drives the real page-model handlers over a fake
/// <see cref="HttpContext"/> and proves, for a representative set of write
/// handlers (a mass-send, a Settings toggle, EditParticipant, a delete, MarkPaid):
///   • a REAL organizer passes the gate,
///   • an acting-as-INTO-organizer session (Role==Organizer, IsActingAs==true)
///     is DENIED,
///   • a secretary-token session is DENIED.
/// FAKE names only.
/// </summary>
public sealed class OrganizerActingAsAuthzTests
{
    private const int EventId = 7;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"actingas-authz-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-18T10:00:00Z");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static IStringLocalizer<SharedResource> Loc()
    {
        var options = Options.Create(new LocalizationOptions { ResourcesPath = "" });
        var factory = new ResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    // --- Session builders ----------------------------------------------------

    /// <summary>A normal, non-acting organizer session.</summary>
    private static DefaultHttpContext RealOrganizer(int participantId = 1)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, participantId.ToString()),
            new(ClaimTypes.Email, "real.organizer@example.test"),
            new(ClaimTypes.Name, "Real Organizer"),
            new(ClaimTypes.Role, ParticipantRole.Organizer.ToString()),
            new("EventId", EventId.ToString()),
        };
        return ContextOf(claims);
    }

    /// <summary>
    /// An acting-as session that has switched INTO another organizer: the
    /// identity (and Role) is the target organizer, but the acting-as markers
    /// are present, so <c>IsActingAs == true</c>. This is the exact session that
    /// the role-only gate failed to stop.
    /// </summary>
    private static DefaultHttpContext ActingAsIntoOrganizer(int targetId = 99)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, targetId.ToString()),
            new(ClaimTypes.Email, "target.organizer@example.test"),
            new(ClaimTypes.Name, "Target Organizer"),
            new(ClaimTypes.Role, ParticipantRole.Organizer.ToString()), // target IS an organizer
            new("EventId", EventId.ToString()),
            new(CommunityHub.Core.Auth.ActingAsClaims.ActorKind, ImpersonationActorKind.Organizer.ToString()),
            new(CommunityHub.Core.Auth.ActingAsClaims.ActorParticipantId, "1"),
            new(CommunityHub.Core.Auth.ActingAsClaims.ActorLabel, "Real Organizer"),
        };
        return ContextOf(claims);
    }

    /// <summary>
    /// A secretary-token session: lands on a participant via a secure-token URL,
    /// marked as a SecretaryToken acting-as session. Should never write
    /// organizer-scoped data. (Built here landing on an organizer to make the
    /// gate the only thing that can stop the write.)
    /// </summary>
    private static DefaultHttpContext SecretaryToken(int targetId = 99)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, targetId.ToString()),
            new(ClaimTypes.Email, "target.organizer@example.test"),
            new(ClaimTypes.Name, "Target Organizer"),
            new(ClaimTypes.Role, ParticipantRole.Organizer.ToString()),
            new("EventId", EventId.ToString()),
            new(CommunityHub.Core.Auth.ActingAsClaims.ActorKind, ImpersonationActorKind.SecretaryToken.ToString()),
            new(CommunityHub.Core.Auth.ActingAsClaims.ActorLabel, "Secretary token"),
        };
        return ContextOf(claims);
    }

    private static DefaultHttpContext ContextOf(List<Claim> claims) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
    };

    private static ICurrentParticipantAccessor Accessor(HttpContext http) =>
        new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));

    // ---------------------------------------------------------------------
    //  Self-check: the acting-as / secretary sessions really do present as
    //  Role==Organizer (so the defect was real) yet IsActingAs==true.
    // ---------------------------------------------------------------------

    [Fact]
    public void ActingAsIntoOrganizer_presents_as_organizer_role_but_is_acting_as()
    {
        var me = CurrentParticipant.FromPrincipal(ActingAsIntoOrganizer().User);
        Assert.NotNull(me);
        Assert.Equal(ParticipantRole.Organizer, me!.Role); // a bare role check would pass
        Assert.True(me.IsActingAs);                         // ...but it must be denied writes
        Assert.False(OrganizerAuth.IsRealOrganizer(me));
    }

    [Fact]
    public void SecretaryToken_session_is_never_a_real_organizer()
    {
        var me = CurrentParticipant.FromPrincipal(SecretaryToken().User);
        Assert.NotNull(me);
        Assert.True(me!.IsActingAs);
        Assert.Equal(ImpersonationActorKind.SecretaryToken, me.ActingAs!.Kind);
        Assert.False(OrganizerAuth.IsRealOrganizer(me));
    }

    [Fact]
    public void RealOrganizer_session_is_a_real_organizer()
    {
        var me = CurrentParticipant.FromPrincipal(RealOrganizer().User);
        Assert.NotNull(me);
        Assert.True(OrganizerAuth.IsRealOrganizer(me));
    }

    // ---------------------------------------------------------------------
    //  Settings kill-switch toggle  (Settings.OnPostToggleAsync)
    // ---------------------------------------------------------------------

    private static SettingsModel NewSettings(CommunityHubDbContext db, HttpContext http, FeatureSettingsService? settings) =>
        new(Accessor(http), settings!, Options.Create(new EmailOptions()), new StubWebHostEnv())
        {
            PageContext = new PageContext { HttpContext = http },
        };

    private sealed class StubWebHostEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "CommunityHub.Tests";
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task SettingsToggle_real_organizer_passes_the_gate()
    {
        using var db = NewDb();
        var http = RealOrganizer();
        // Blank key skips the mutate; LoadAsync still runs, so give a real service.
        var model = NewSettings(db, http, new FeatureSettingsService(db, new FixedClock()));

        var result = await model.OnPostToggleAsync(key: "", enable: true, CancellationToken.None);

        Assert.IsNotType<ForbidResult>(result); // past the gate
    }

    [Fact]
    public async Task SettingsToggle_acting_as_organizer_is_denied()
    {
        using var db = NewDb();
        var model = NewSettings(db, ActingAsIntoOrganizer(), settings: null); // gate fires first

        var result = await model.OnPostToggleAsync(key: "RegistrationOpen", enable: false, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task SettingsToggle_secretary_token_is_denied()
    {
        using var db = NewDb();
        var model = NewSettings(db, SecretaryToken(), settings: null);

        var result = await model.OnPostToggleAsync(key: "RegistrationOpen", enable: false, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    // ---------------------------------------------------------------------
    //  EditParticipant role/email escalation  (EditParticipant.OnPostAsync)
    // ---------------------------------------------------------------------

    private static EditParticipantModel NewEditParticipant(CommunityHubDbContext db, HttpContext http) =>
        new(db, Accessor(http), welcome: null!, new FixedClock())
        {
            PageContext = new PageContext { HttpContext = http },
        };

    [Fact]
    public async Task EditParticipant_real_organizer_passes_the_gate()
    {
        using var db = NewDb();
        var model = NewEditParticipant(db, RealOrganizer());
        model.Email = "";   // past the gate, then stops at email validation (no service touched)

        var result = await model.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);          // NOT a role/acting-as denial
        Assert.Equal("A valid email is required.", model.Error);
    }

    [Fact]
    public async Task EditParticipant_acting_as_organizer_is_denied()
    {
        using var db = NewDb();
        var model = NewEditParticipant(db, ActingAsIntoOrganizer());
        model.Email = "escalate@example.test";
        model.FullName = "Escalated";
        model.Role = ParticipantRole.Organizer;

        var result = await model.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
    }

    [Fact]
    public async Task EditParticipant_secretary_token_is_denied()
    {
        using var db = NewDb();
        var model = NewEditParticipant(db, SecretaryToken());
        model.Email = "escalate@example.test";
        model.FullName = "Escalated";

        var result = await model.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
    }

    // ---------------------------------------------------------------------
    //  TravelReimbursements MarkPaid  (TravelReimbursements.OnPostMarkPaidAsync)
    // ---------------------------------------------------------------------

    private static TravelReimbursementsModel NewTravel(CommunityHubDbContext db, HttpContext http) =>
        new(db, Accessor(http), new FixedClock(), emailSender: null!, templates: null!,
            activeEvent: null!, logger: NullLogger<TravelReimbursementsModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = http },
        };

    [Fact]
    public async Task MarkPaid_real_organizer_passes_the_gate()
    {
        using var db = NewDb();
        var model = NewTravel(db, RealOrganizer());

        // Past the gate; empty DB ⇒ row lookup is null ⇒ returns Page() without
        // touching the email services. The point is it is NOT access-denied.
        var result = await model.OnPostMarkPaidAsync(id: 12345, notes: null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.AccessDenied);
    }

    [Fact]
    public async Task MarkPaid_acting_as_organizer_is_denied()
    {
        using var db = NewDb();
        var model = NewTravel(db, ActingAsIntoOrganizer());

        var result = await model.OnPostMarkPaidAsync(id: 12345, notes: null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
    }

    [Fact]
    public async Task MarkPaid_secretary_token_is_denied()
    {
        using var db = NewDb();
        var model = NewTravel(db, SecretaryToken());

        var result = await model.OnPostMarkPaidAsync(id: 12345, notes: null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
    }

    // ---------------------------------------------------------------------
    //  A delete  (PreselectionQueue.OnPostDeleteAsync)
    // ---------------------------------------------------------------------

    private static PreselectionQueueModel NewPreselection(
        CommunityHubDbContext db, HttpContext http, ParticipantDeletionService? deletion) =>
        new(Accessor(http), queue: null!, activation: null!, deletion: deletion!)
        {
            PageContext = new PageContext { HttpContext = http },
        };

    [Fact]
    public async Task Delete_real_organizer_passes_the_gate()
    {
        using var db = NewDb();
        // Past the gate ⇒ HardDeleteAsync runs against an empty DB (NotFound),
        // so a real deletion service is supplied.
        var model = NewPreselection(db, RealOrganizer(),
            new ParticipantDeletionService(db, new FixedClock()));

        var result = await model.OnPostDeleteAsync(participantId: 12345, CancellationToken.None);

        Assert.IsNotType<ForbidResult>(result); // past the gate
    }

    [Fact]
    public async Task Delete_acting_as_organizer_is_denied()
    {
        using var db = NewDb();
        var model = NewPreselection(db, ActingAsIntoOrganizer(), deletion: null); // gate fires first

        var result = await model.OnPostDeleteAsync(participantId: 12345, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Delete_secretary_token_is_denied()
    {
        using var db = NewDb();
        var model = NewPreselection(db, SecretaryToken(), deletion: null);

        var result = await model.OnPostDeleteAsync(participantId: 12345, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    // ---------------------------------------------------------------------
    //  A mass-send  (Broadcast.OnPostSendAsync) — deny only
    //  (the send path needs the full email pipeline; the security contract is
    //   that an acting-as / secretary session never reaches it).
    // ---------------------------------------------------------------------

    private static BroadcastModel NewBroadcast(CommunityHubDbContext db, HttpContext http) =>
        new(db, Accessor(http), templates: null!, emailSender: null!, new FixedClock(),
            NullLogger<BroadcastModel>.Instance, Loc())
        {
            PageContext = new PageContext { HttpContext = http },
        };

    [Fact]
    public async Task MassSend_acting_as_organizer_is_denied()
    {
        using var db = NewDb();
        var model = NewBroadcast(db, ActingAsIntoOrganizer());

        var result = await model.OnPostSendAsync(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task MassSend_secretary_token_is_denied()
    {
        using var db = NewDb();
        var model = NewBroadcast(db, SecretaryToken());

        var result = await model.OnPostSendAsync(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }
}
