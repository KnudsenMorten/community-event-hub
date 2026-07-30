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

    private static SessionizeImportModel NewSessionizeImport(
        CommunityHubDbContext db, HttpContext http,
        CommunityHub.Core.Reminders.ISessionizeApiImportService? apiImport = null)
    {
        // API options enabled so the gate (not the config) is what stops the run.
        var apiOptions = new CommunityHub.Core.Integrations.SessionizeApiOptions
        {
            Enabled = true, EndpointId = "endpoint-x",
        };
        return new SessionizeImportModel(
            Accessor(http), apiImport: apiImport!, preview: null!,
            apiOptions, new FeatureGateService(db), new RingResolver(db))
        {
            PageContext = new PageContext { HttpContext = (DefaultHttpContext)http },
        };
    }

    /// <summary>
    /// §198 fake: records the on-demand delta import call and returns canned counts +
    /// a skipped reason, so the page test can prove the handler ran the SAME service the
    /// timer job runs (delta, never emails) and rendered the result.
    /// </summary>
    private sealed class RecordingApiImport : CommunityHub.Core.Reminders.ISessionizeApiImportService
    {
        public int Calls { get; private set; }
        public bool? LastSendWelcome { get; private set; }
        public CommunityHub.Core.Reminders.SessionizeImportMode? LastMode { get; private set; }

        public Task<CommunityHub.Core.Reminders.SessionizeImportResult> ImportAsync(
            int eventId,
            CancellationToken ct = default,
            bool sendWelcome = false,
            CommunityHub.Core.Reminders.SessionizeImportMode mode =
                CommunityHub.Core.Reminders.SessionizeImportMode.Delta)
        {
            Calls++;
            LastSendWelcome = sendWelcome;
            LastMode = mode;
            return Task.FromResult(new CommunityHub.Core.Reminders.SessionizeImportResult(
                Fetched: 5, Created: 2, Updated: 1, Skipped: 2,
                Warnings: new[] { "Skipped \"No Email Speaker\" — no email on the Sessionize view." },
                Error: null));
        }
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

    // §198: the on-demand "Sync new speakers (delta)" button runs the SAME import the
    // timer job runs, in-request, never emails, and renders read/created/updated/skipped
    // counts + skipped reasons.
    [Fact]
    public async Task Sessionize_force_sync_runs_import_in_request_and_renders_counts()
    {
        using var db = NewDb();
        await EnableAsync(db, "sessionize-import");
        var http = OrganizerContext();
        var fake = new RecordingApiImport();
        var model = NewSessionizeImport(db, http, fake);

        var result = await model.OnPostApiAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        // Ran the import service exactly once, synchronously in-request, as a DELTA that
        // never sends welcome emails (the §198 safety).
        Assert.Equal(1, fake.Calls);
        Assert.Equal(false, fake.LastSendWelcome);
        Assert.Equal(CommunityHub.Core.Reminders.SessionizeImportMode.Delta, fake.LastMode);
        // Result is surfaced for rendering: read/created/updated/skipped + skipped reasons.
        Assert.NotNull(model.Result);
        Assert.Null(model.Result!.Error);
        Assert.Equal(5, model.Result.Fetched);
        Assert.Equal(2, model.Result.Created);
        Assert.Equal(1, model.Result.Updated);
        Assert.Equal(2, model.Result.Skipped);
        Assert.Contains(model.Result.Warnings, w => w.Contains("no email"));
        Assert.False(model.ResultWasFullImport);   // delta, not full
        Assert.Null(model.ValidationError);
    }

    [Fact]
    public async Task Sessionize_force_sync_shows_not_configured_when_api_disabled()
    {
        using var db = NewDb();
        await EnableAsync(db, "sessionize-import");
        var http = OrganizerContext();
        var fake = new RecordingApiImport();
        // API options disabled ⇒ ApiEnabled is false ⇒ existing not-configured message.
        var model = new SessionizeImportModel(
            Accessor(http), apiImport: fake, preview: null!,
            new CommunityHub.Core.Integrations.SessionizeApiOptions { Enabled = false },
            new FeatureGateService(db), new RingResolver(db))
        {
            PageContext = new PageContext { HttpContext = http },
        };

        var result = await model.OnPostApiAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(0, fake.Calls);             // never reached the import service
        Assert.Null(model.Result);
        Assert.NotNull(model.ValidationError);
        Assert.Contains("not configured", model.ValidationError!);
    }

    // §197: the legacy Speakers-page Excel/xlsx import (and its feature gate) was
    // removed — speakers come from the Sessionize API import only — so the former
    // "Speakers_excel_import_noops_when_feature_disabled" test was dropped with it.

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
            emailSender: null!, new FixedClock(), new FeatureGateService(db),
            // Company Manager lookups are disabled (Enabled=false default) — the
            // handlers under test never call the client, so a null client is safe.
            cm: null!, cmOptions: new CommunityHub.Core.Integrations.CompanyManagerOptions(),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<
                CommunityHub.Pages.Organizer.SponsorAdmin.LeadsModel>.Instance)
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

    // (The Sessions master-class Zoho Booking sync was RETIRED — CEH owns MC seats
    //  via MasterClassSignup; its gate test was removed with the handler.)
}
