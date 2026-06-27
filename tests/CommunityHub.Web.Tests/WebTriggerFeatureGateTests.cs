using System.Security.Claims;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
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
/// The §23 RELEASE-GATE half for the in-request WEB TRIGGERS: an organizer's
/// manual "import / pull / sync now" button must honour the SAME per-edition
/// kill switch the scheduled job does — GUI state == actual behaviour. Each test
/// drives the real page-model POST handler over a fake organizer session with the
/// integration services passed as <c>null!</c>, so the disabled handler can only
/// return without a NullReferenceException when the gate short-circuited first
/// (and it surfaces the "feature disabled" message). One test per gated trigger
/// also proves the handler proceeds PAST the gate once the switch is ON.
///
/// Pairs with JobFeatureGateTests (the timer-job half) and FeatureGateServiceTests
/// (gate resolution) in the Core test project. FAKE names only.
/// </summary>
public sealed class WebTriggerFeatureGateTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"webgate-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-15T10:00:00Z");
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    private static DefaultHttpContext OrganizerContext()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "1"),
            new(ClaimTypes.Email, "org@example.test"),
            new(ClaimTypes.Name, "Olive Organizer"),
            new(ClaimTypes.Role, ParticipantRole.Organizer.ToString()),
            new("EventId", EventId.ToString()),
        };
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
        };
    }

    private static ICurrentParticipantAccessor Accessor(HttpContext http) =>
        new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http));

    private static IStringLocalizer<SharedResource> Loc()
    {
        var options = Options.Create(new LocalizationOptions { ResourcesPath = "" });
        var factory = new ResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
        return new StringLocalizer<SharedResource>(factory);
    }

    private static async Task EnableAsync(CommunityHubDbContext db, string key)
    {
        var settings = new FeatureSettingsService(db, new FixedClock());
        await settings.SetEnabledAsync(EventId, key, true, "org@expertslive.dk");
        // These are ENABLED-gate tests (ring gating is covered separately). Since the
        // controlled-rollout default is now Ring1 and the test organizer has no DB
        // participant (⇒ effective ring Broad), release the feature to Broad so the
        // ring check never interferes with the enabled/disabled assertion.
        await settings.SetReleasedRingAsync(EventId, key, CommunityHub.Core.Settings.Ring.Broad, null);
    }

    // ---- Sessionize import page ('sessionize-import') ----------------------

    private static SessionizeImportModel NewSessionizeImport(CommunityHubDbContext db, HttpContext http)
    {
        // API options enabled so the gate (not the config) is what stops the run.
        var apiOptions = new CommunityHub.Core.Integrations.SessionizeApiOptions
        {
            Enabled = true, EndpointId = "endpoint-x",
        };
        return new SessionizeImportModel(
            Accessor(http), apiImport: null!, preview: null!,
            apiOptions, new FeatureGateService(db), new RingResolver(db))
        {
            PageContext = new PageContext { HttpContext = (DefaultHttpContext)http },
        };
    }

    [Fact]
    public async Task SessionizeImport_api_commit_noops_when_feature_disabled()
    {
        using var db = NewDb();
        var http = OrganizerContext();
        var model = NewSessionizeImport(db, http);

        var result = await model.OnPostApiAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Null(model.Result);
        Assert.NotNull(model.ValidationError);
        Assert.Contains("turned off", model.ValidationError!);
    }

    [Fact]
    public async Task SessionizeImport_api_commit_runs_past_the_gate_when_enabled()
    {
        using var db = NewDb();
        await EnableAsync(db, "sessionize-import");
        var http = OrganizerContext();
        var model = NewSessionizeImport(db, http);

        // Enabled ⇒ the handler proceeds to the (null) import service and throws —
        // proof the gate let it through (the disabled run above did NOT throw).
        await Assert.ThrowsAsync<NullReferenceException>(
            () => model.OnPostApiAsync(CancellationToken.None));
    }

    // ---- Speakers page Excel import ('sessionize-import') ------------------

    [Fact]
    public async Task Speakers_excel_import_noops_when_feature_disabled()
    {
        using var db = NewDb();
        var http = OrganizerContext();
        var model = new SpeakersModel(
            db, Accessor(http), new FixedClock(),
            new CommunityHub.Core.Organizer.SpeakerDeletionService(db),
            new FeatureGateService(db))
        {
            PageContext = new PageContext { HttpContext = http },
        };

        var result = await model.OnPostImportXlsxAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.Error);
        Assert.Contains("turned off", model.Error!);
    }

    // ---- Sponsor leads "sync now" ('sponsor-leads') -----------------------

    private sealed class NullTempDataProvider : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static CommunityHub.Pages.Organizer.SponsorAdmin.LeadsModel NewLeads(
        CommunityHubDbContext db, HttpContext http) =>
        new(db, Accessor(http), keys: null!, detTokens: null!, sync: null!,
            emailSender: null!, new FixedClock(), new FeatureGateService(db))
        {
            PageContext = new PageContext { HttpContext = http },
            TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
                http, new NullTempDataProvider()),
        };

    [Fact]
    public async Task SponsorLeads_sync_now_noops_when_feature_disabled()
    {
        using var db = NewDb();
        var http = OrganizerContext();
        var model = NewLeads(db, http);

        var result = await model.OnPostSyncNowAsync(CancellationToken.None);

        // Disabled ⇒ a clear "turned off" notice + a redirect, no _sync call (null).
        Assert.IsType<RedirectToPageResult>(result);
        Assert.NotNull(model.TempData["Notice"]);
        Assert.Contains("turned off", model.TempData["Notice"]!.ToString()!);
    }

    [Fact]
    public async Task SponsorLeads_sync_now_runs_past_the_gate_when_enabled()
    {
        using var db = NewDb();
        await EnableAsync(db, "sponsor-leads");
        var http = OrganizerContext();
        var model = NewLeads(db, http);

        await Assert.ThrowsAsync<NullReferenceException>(
            () => model.OnPostSyncNowAsync(CancellationToken.None));
    }

    // ---- Sessions master-class booking sync ('attendee-reconcile') --------

    private static SessionsModel NewSessions(CommunityHubDbContext db, HttpContext http) =>
        new(db, Accessor(http), mgmt: null!, evalMail: null!, logistics: null!,
            bookingSync: null!, eval: null!, deletion: null!, bulk: null!,
            new FixedClock(), Loc(), new FeatureGateService(db))
        {
            PageContext = new PageContext { HttpContext = http },
        };

    [Fact]
    public async Task Sessions_booking_sync_noops_when_feature_disabled()
    {
        using var db = NewDb();
        var http = OrganizerContext();
        var model = NewSessions(db, http);

        var result = await model.OnPostSyncBookingsAsync(CancellationToken.None);

        // Disabled ⇒ a NoOp action result (NOT a green success), no _bookingSync call.
        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.Result);
        Assert.Contains("turned off", model.Result!.Message);
    }
}
