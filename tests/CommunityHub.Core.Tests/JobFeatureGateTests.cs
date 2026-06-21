using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using CommunityHub.Jobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The §23 RELEASE-GATE half for the timer JOBS: a disabled advanced feature must
/// make the scheduled job NO-OP before it touches any integration service. Each
/// test drives the real <c>Run()</c> with the integration services passed as
/// <c>null!</c> — so the run can ONLY complete without a NullReferenceException
/// when the feature gate short-circuited first. The same guard is asserted to let
/// the run proceed (gate resolves true) once the per-edition switch is ON.
///
/// This is the per-job complement to <see cref="FeatureGateServiceTests"/> (gate
/// resolution) and <see cref="FeatureCatalogClassificationTests"/> (catalog
/// classification): together they prove "GUI state == actual behaviour" for every
/// newly-gated job (REQUIREMENTS §23 residual: BackstageSync was already wired;
/// this covers Sessionize import, sponsor order pull, sponsor leads, sponsor
/// upload watch, and attendee reconcile).
/// </summary>
public sealed class JobFeatureGateTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"jobgate-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> SeedActiveEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "GATE27", CommunityName = "Gate", DisplayName = "Gate 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static FeatureGateService Gate(CommunityHubDbContext db) => new(db);

    private static async Task EnableAsync(CommunityHubDbContext db, int eventId, string key)
    {
        var settings = new FeatureSettingsService(db, new FixedClock(Now));
        await settings.SetEnabledAsync(eventId, key, true, "org@expertslive.dk");
    }

    private static TimerInfo Timer() => new();

    // ---- SessionizeImportJob ('sessionize-import') -------------------------

    [Fact]
    public async Task SessionizeImportJob_skips_when_feature_disabled()
    {
        using var db = NewDb();
        await SeedActiveEventAsync(db);

        // Config is on, but the per-edition gate defaults OFF, so the run must
        // no-op BEFORE _service.ImportAsync — proven by null services not throwing.
        var options = new CommunityHub.Core.Integrations.SessionizeApiOptions { Enabled = true };
        var job = new SessionizeImportJob(service: null!, options, db, Gate(db),
            NullLogger<SessionizeImportJob>.Instance);

        await job.Run(Timer(), default); // no throw == gate short-circuited
    }

    [Fact]
    public async Task SessionizeImportJob_runs_past_the_gate_when_enabled()
    {
        using var db = NewDb();
        var eventId = await SeedActiveEventAsync(db);
        await EnableAsync(db, eventId, "sessionize-import");

        var options = new CommunityHub.Core.Integrations.SessionizeApiOptions { Enabled = true };
        var job = new SessionizeImportJob(service: null!, options, db, Gate(db),
            NullLogger<SessionizeImportJob>.Instance);

        // Enabled ⇒ the run proceeds to the (null) import service and throws —
        // proof the gate let it through (the disabled run above did NOT throw).
        await Assert.ThrowsAsync<NullReferenceException>(() => job.Run(Timer(), default));
    }

    // ---- AttendeeReconcileJob ('attendee-reconcile') -----------------------

    [Fact]
    public async Task AttendeeReconcileJob_skips_when_feature_disabled()
    {
        using var db = NewDb();
        await SeedActiveEventAsync(db);

        var options = new CommunityHub.Core.Integrations.ZohoOptions { Enabled = true };
        var job = new AttendeeReconcileJob(db, zoho: null!, options, reconciler: null!,
            engine: null!, templates: null!, new FixedClock(Now), Gate(db),
            NullLogger<AttendeeReconcileJob>.Instance);

        await job.Run(Timer(), default); // no throw == gate short-circuited
    }

    [Fact]
    public async Task AttendeeReconcileJob_runs_past_the_gate_when_enabled()
    {
        using var db = NewDb();
        var eventId = await SeedActiveEventAsync(db);
        await EnableAsync(db, eventId, "attendee-reconcile");

        var options = new CommunityHub.Core.Integrations.ZohoOptions { Enabled = true };
        var job = new AttendeeReconcileJob(db, zoho: null!, options, reconciler: null!,
            engine: null!, templates: null!, new FixedClock(Now), Gate(db),
            NullLogger<AttendeeReconcileJob>.Instance);

        await Assert.ThrowsAsync<NullReferenceException>(() => job.Run(Timer(), default));
    }

    // ---- SponsorLeadsJob ('sponsor-leads') ---------------------------------

    [Fact]
    public async Task SponsorLeadsJob_skips_when_feature_disabled()
    {
        using var db = NewDb();
        await SeedActiveEventAsync(db);

        var zoho = new CommunityHub.Core.Integrations.ZohoOptions { CrmEnabled = true };
        var job = new SponsorLeadsJob(db, sync: null!, zoho, templates: null!,
            emailSender: null!, new FixedClock(Now), Gate(db),
            NullLogger<SponsorLeadsJob>.Instance);

        await job.Run(Timer(), default); // no throw == gate short-circuited
    }

    [Fact]
    public async Task SponsorLeadsJob_runs_past_the_gate_when_enabled()
    {
        using var db = NewDb();
        var eventId = await SeedActiveEventAsync(db);
        await EnableAsync(db, eventId, "sponsor-leads");

        // Force the 05:00 CRM-sync branch so the run reaches the null sync service.
        var zoho = new CommunityHub.Core.Integrations.ZohoOptions { CrmEnabled = true };
        var job = new SponsorLeadsJob(db, sync: null!, zoho, templates: null!,
            emailSender: null!, new FixedClock(new DateTimeOffset(2027, 1, 15, 5, 0, 0, TimeSpan.Zero)),
            Gate(db), NullLogger<SponsorLeadsJob>.Instance);

        await Assert.ThrowsAsync<NullReferenceException>(() => job.Run(Timer(), default));
    }

    // ---- WooCommercePullJob ('sponsor-order-pull') -------------------------

    [Fact]
    public async Task WooCommercePullJob_skips_when_feature_disabled_for_all_editions()
    {
        using var db = NewDb();
        await SeedActiveEventAsync(db);

        var job = new WooCommercePullJob(service: null!, db, Gate(db),
            NullLogger<WooCommercePullJob>.Instance);

        await job.Run(Timer(), default); // no throw == gate short-circuited
    }

    [Fact]
    public async Task WooCommercePullJob_runs_past_the_gate_when_any_edition_enabled()
    {
        using var db = NewDb();
        var eventId = await SeedActiveEventAsync(db);
        await EnableAsync(db, eventId, "sponsor-order-pull");

        var job = new WooCommercePullJob(service: null!, db, Gate(db),
            NullLogger<WooCommercePullJob>.Instance);

        await Assert.ThrowsAsync<NullReferenceException>(() => job.Run(Timer(), default));
    }

    // ---- SponsorUploadWatchJob ('sponsor-upload-watch') --------------------

    [Fact]
    public async Task SponsorUploadWatchJob_skips_when_feature_disabled_for_all_editions()
    {
        using var db = NewDb();
        await SeedActiveEventAsync(db);

        var job = new SponsorUploadWatchJob(watch: null!, db, Gate(db),
            NullLogger<SponsorUploadWatchJob>.Instance);

        await job.Run(Timer(), default); // no throw == gate short-circuited
    }

    [Fact]
    public async Task SponsorUploadWatchJob_runs_past_the_gate_when_any_edition_enabled()
    {
        using var db = NewDb();
        var eventId = await SeedActiveEventAsync(db);
        await EnableAsync(db, eventId, "sponsor-upload-watch");

        var job = new SponsorUploadWatchJob(watch: null!, db, Gate(db),
            NullLogger<SponsorUploadWatchJob>.Instance);

        await Assert.ThrowsAsync<NullReferenceException>(() => job.Run(Timer(), default));
    }
}
