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
        new(Accessor(http), settings!, new CommunityHub.Core.Settings.FeatureGateService(db), Options.Create(new EmailOptions()), new StubWebHostEnv(),
            new CommunityHub.Core.Integrations.ExternalWriteOptions(),
            // §515: the per-template ring service the page lists every e-mail from.
            new CommunityHub.Core.Email.EmailTemplateRingService(
                db, new CommunityHub.Core.Settings.FeatureGateService(db), new FixedClock()),
            // §703: the DEV/PROD badge source (not IWebHostEnvironment — see HubEnvironment).
            new CommunityHub.Core.Diagnostics.HubEnvironment("DEV", null))
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
        new(db, Accessor(http), welcome: null!, attendeeWelcome: null!, new FixedClock())
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
        new(Accessor(http), queue: null!, activation: null!, deletion: deletion!, availability: null!)
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
            new ParticipantDeletionService(db, new FixedClock(),
                new ParticipantDeactivationService(db, new FixedClock(),
                    new CommunityHub.Core.Audit.AuditTrailService(db, new FixedClock()))));

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
    //  🗑 §705.12 — the mass-send (Broadcast) acting-as/secretary DENY tests were removed with the
    //  Broadcast page itself (operator 2026-07-29: "you are welcome to delete broadcast as i will
    //  newer use it"). Verified unused before deleting: PROD SentReminders held ZERO broadcast rows.
    //
    //  🔒 NOTE FOR ANYONE RESTORING A MASS-SEND: these two tests existed because an acting-as or
    //  secretary session must NEVER reach a mass-send path. That contract is not specific to
    //  Broadcast — re-create the equivalent tests for whatever replaces it. The remaining
    //  acting-as/secretary deny tests in this file are the pattern to copy.
    // ---------------------------------------------------------------------

    // ---------------------------------------------------------------------
    //  §234 4d sweep: welcome-link REVOKE (WelcomeLinks.OnPostRevokeAsync) —
    //  killing (or NOT killing) a live sign-in link is a security write.
    // ---------------------------------------------------------------------

    private static WelcomeLinksModel NewWelcomeLinks(
        CommunityHubDbContext db, HttpContext http,
        CommunityHub.Core.Auth.WelcomeGrantAdminService? admin) =>
        new(Accessor(http), admin!, emailLinks: null!)
        {
            PageContext = new PageContext { HttpContext = http },
        };

    [Fact]
    public async Task WelcomeLinkRevoke_real_organizer_passes_the_gate()
    {
        using var db = NewDb();
        // Past the gate ⇒ RevokeAsync runs against an empty DB (unknown id ⇒ false)
        // and redirects with the "could not be revoked" message — NOT access-denied.
        var model = NewWelcomeLinks(db, RealOrganizer(),
            new CommunityHub.Core.Auth.WelcomeGrantAdminService(db));

        var result = await model.OnPostRevokeAsync(id: 12345, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.False(model.AccessDenied);
    }

    [Fact]
    public async Task WelcomeLinkRevoke_acting_as_organizer_is_denied()
    {
        using var db = NewDb();
        var model = NewWelcomeLinks(db, ActingAsIntoOrganizer(), admin: null); // gate fires first

        var result = await model.OnPostRevokeAsync(id: 12345, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
    }

    [Fact]
    public async Task WelcomeLinkRevoke_secretary_token_is_denied()
    {
        using var db = NewDb();
        var model = NewWelcomeLinks(db, SecretaryToken(), admin: null);

        var result = await model.OnPostRevokeAsync(id: 12345, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
    }

    // ---------------------------------------------------------------------
    //  §234 4d sweep: quiz authoring write (Quizzes.OnPostUpdateQuizAsync)
    // ---------------------------------------------------------------------

    private static QuizzesModel NewQuizzes(
        CommunityHubDbContext db, HttpContext http,
        CommunityHub.Core.Quizzes.QuizAuthoringService? authoring) =>
        new(Accessor(http), authoring!, seeder: null!)
        {
            PageContext = new PageContext { HttpContext = http },
        };

    [Fact]
    public async Task QuizUpdate_real_organizer_passes_the_gate()
    {
        using var db = NewDb();
        // Past the gate ⇒ UpdateQuizAsync no-ops on the unknown id and redirects.
        var model = NewQuizzes(db, RealOrganizer(),
            new CommunityHub.Core.Quizzes.QuizAuthoringService(db, new FixedClock()));

        var result = await model.OnPostUpdateQuizAsync(
            quizId: 12345, title: "T", isActive: true,
            questionsPerAttempt: 3, perQuestionSeconds: 20, basePoints: 100,
            CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.False(model.AccessDenied);
    }

    [Fact]
    public async Task QuizUpdate_acting_as_organizer_is_denied()
    {
        using var db = NewDb();
        var model = NewQuizzes(db, ActingAsIntoOrganizer(), authoring: null); // gate fires first

        var result = await model.OnPostUpdateQuizAsync(
            quizId: 12345, title: "T", isActive: true,
            questionsPerAttempt: 3, perQuestionSeconds: 20, basePoints: 100,
            CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
    }

    [Fact]
    public async Task QuizUpdate_secretary_token_is_denied()
    {
        using var db = NewDb();
        var model = NewQuizzes(db, SecretaryToken(), authoring: null);

        var result = await model.OnPostUpdateQuizAsync(
            quizId: 12345, title: "T", isActive: true,
            questionsPerAttempt: 3, perQuestionSeconds: 20, basePoints: 100,
            CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.AccessDenied);
    }

    // ---------------------------------------------------------------------
    //  §337 — THE ALLOCATION COMMITS. These two handlers were the worst of the
    //  ten unguarded ones: Commit assigns real people to real shifts AND sends
    //  notification e-mail (NotifyCommitAsync), with the audit trail attributing
    //  all of it to the impersonated organizer.
    //
    //  Delegating the check to the service was NOT a gate: both services tested
    //  only "actor.Role != Organizer", which an acting-as session satisfies (it
    //  carries the TARGET's Organizer role). Every collaborator below is passed
    //  as null! ON PURPOSE — the guard must return before anything is touched,
    //  so a regression throws instead of quietly passing (the §334 precedent).
    // ---------------------------------------------------------------------

    private static BucketAllocationModel NewBucketAllocation(
        CommunityHubDbContext db, HttpContext http) =>
        new(db, Accessor(http), alloc: null!, import: null!, parser: null!,
            guidance: null!, engine: null!, notify: null!,
            logger: NullLogger<BucketAllocationModel>.Instance)
        { PageContext = new PageContext { HttpContext = http } };

    private static OrganizerAllocationModel NewOrganizerAllocation(
        CommunityHubDbContext db, HttpContext http) =>
        new(db, Accessor(http), alloc: null!, engine: null!, notify: null!,
            guidance: null!, logger: NullLogger<OrganizerAllocationModel>.Instance)
        { PageContext = new PageContext { HttpContext = http } };

    [Fact]
    public async Task VolunteerAllocationCommit_acting_as_organizer_is_denied()
    {
        using var db = NewDb();
        var model = NewBucketAllocation(db, ActingAsIntoOrganizer());

        var result = await model.OnPostCommitAsync(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task VolunteerAllocationCommit_secretary_token_is_denied()
    {
        using var db = NewDb();
        var model = NewBucketAllocation(db, SecretaryToken());

        var result = await model.OnPostCommitAsync(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task OrganizerAllocationCommit_acting_as_organizer_is_denied()
    {
        using var db = NewDb();
        var model = NewOrganizerAllocation(db, ActingAsIntoOrganizer());

        var result = await model.OnPostCommitAsync(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task OrganizerAllocationCommit_secretary_token_is_denied()
    {
        using var db = NewDb();
        var model = NewOrganizerAllocation(db, SecretaryToken());

        var result = await model.OnPostCommitAsync(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    /// <summary>
    /// The DRAFT handlers matter too: AddDraft / RemoveDraft / Discard /
    /// ReplanDropout / Complete were all unguarded, and the draft an impersonated
    /// organizer builds is exactly what the next Commit turns into real assignments.
    /// </summary>
    [Fact]
    public async Task VolunteerAllocationAddDraft_acting_as_organizer_is_denied()
    {
        using var db = NewDb();
        var model = NewBucketAllocation(db, ActingAsIntoOrganizer());

        var result = await model.OnPostAddDraftAsync(taskId: 1, volunteerId: 2, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task VolunteerAllocationReplanDropout_acting_as_organizer_is_denied()
    {
        using var db = NewDb();
        var model = NewBucketAllocation(db, ActingAsIntoOrganizer());

        var result = await model.OnPostReplanDropoutAsync(volunteerId: 2, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    /// <summary>
    /// §337 DEFENCE IN DEPTH: even if a page guard is later removed, the SERVICE
    /// must refuse. <c>ActorContext</c> now carries the acting-as marker and
    /// <c>RequireOrganizer</c> fails closed on it — so the fix survives a page
    /// being rewritten by someone who never reads this file.
    /// </summary>
    [Fact]
    public async Task ActorContext_carrying_acting_as_is_rejected_by_the_allocation_services()
    {
        var actingAs = new CommunityHub.Core.Domain.VolunteerStructureService.ActorContext(
            99, "target.organizer@example.test", ParticipantRole.Organizer, EventId,
            IsActingAs: true);

        // Role alone would pass — this is exactly why the marker had to exist.
        Assert.Equal(ParticipantRole.Organizer, actingAs.Role);
        Assert.True(actingAs.IsActingAs);

        using var db = NewDb();
        var gate = new FeatureGateService(db);
        var rings = new RingResolver(db);
        var volunteers = new CommunityHub.Core.Volunteers.VolunteerAllocationService(
            db, new FixedClock(), gate, rings);
        var organizers = new CommunityHub.Core.Volunteers.OrganizerAllocationService(
            db, new FixedClock(), gate, rings);

        await Assert.ThrowsAsync<CommunityHub.Core.Domain.VolunteerAccessDeniedException>(
            () => volunteers.CommitAsync(actingAs, CancellationToken.None));
        await Assert.ThrowsAsync<CommunityHub.Core.Domain.VolunteerAccessDeniedException>(
            () => organizers.CommitAsync(actingAs, CancellationToken.None));
    }
}
