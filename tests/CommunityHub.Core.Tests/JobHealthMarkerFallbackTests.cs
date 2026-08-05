using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §786.8 — <c>/Organizer/Jobs</c> must show "last succeeded" for a job whose health marker is
/// written under its FUNCTION NAME while the catalog declares a different <c>HealthKey</c>.
/// </summary>
/// <remarks>
/// <para>🔴 <b>The blank column was on the wrong job to be cosmetic.</b> `WebshopInvoiceJob` and
/// `CouponInvoiceJob` both declare a <c>HealthKey</c> equal to their FEATURE key, but only
/// <c>EngineErrorAlertMiddleware</c> writes their marker — and it keys on the function name. Since
/// §786.4 the webshop job is the <b>only</b> system invoicing those orders, so "last succeeded: —"
/// on that row is the opposite of what the page exists to say.</para>
///
/// <para>🔒 The HealthKey still wins when its row exists: a service that writes its own health-key
/// row does so because it swallows its own failures, which makes that row the truthful one.</para>
/// </remarks>
public sealed class JobHealthMarkerFallbackTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"jobhealth-{Guid.NewGuid():N}").Options);

    private static async Task SeedEventAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "JH27", CommunityName = "C", DisplayName = "Job health",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private static JobScheduleService NewService(CommunityHubDbContext db) =>
        new(db, new FeatureGateService(db), new FixedClock());

    /// <summary>A job whose catalog entry declares a HealthKey nobody writes. This is the shape.</summary>
    private static JobDescriptor JobWithUnwrittenHealthKey() =>
        JobCatalog.All.First(j => j.FunctionName == "WebshopInvoiceJob");

    [Fact]
    public async Task A_marker_written_under_the_function_name_still_reaches_the_page()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        var job = JobWithUnwrittenHealthKey();
        Assert.NotNull(job.HealthKey);
        Assert.NotEqual(job.FunctionName, job.HealthKey);   // the shape this test is about

        // Only the middleware's row exists — keyed on the FUNCTION NAME.
        db.JobHealthMarkers.Add(new JobHealthMarker
        {
            JobKey = job.FunctionName, LastSuccessAt = Now.AddMinutes(-10),
        });
        await db.SaveChangesAsync();

        var rows = await NewService(db).BuildAsync(EventId);
        var row = Assert.Single(rows, r => r.Job.FunctionName == job.FunctionName);

        Assert.Equal(Now.AddMinutes(-10), row.LastSuccessAt);
    }

    /// <summary>
    /// 🔒 When BOTH rows exist the HealthKey one wins — it is written by a service that handles its
    /// own failures, so the middleware would have recorded the same run as a success.
    /// </summary>
    [Fact]
    public async Task The_health_key_row_wins_when_both_exist()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        var job = JobWithUnwrittenHealthKey();
        db.JobHealthMarkers.Add(new JobHealthMarker
        {
            JobKey = job.HealthKey!, LastSuccessAt = Now.AddHours(-1),
        });
        db.JobHealthMarkers.Add(new JobHealthMarker
        {
            JobKey = job.FunctionName, LastSuccessAt = Now.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var rows = await NewService(db).BuildAsync(EventId);
        var row = Assert.Single(rows, r => r.Job.FunctionName == job.FunctionName);

        Assert.Equal(Now.AddHours(-1), row.LastSuccessAt);
    }

    /// <summary>
    /// ⚠️ No marker at all still reads as "never reported", not as a success. A job that has never
    /// run must not look healthy because of a fallback.
    /// </summary>
    [Fact]
    public async Task No_marker_at_all_still_reports_nothing()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        var rows = await NewService(db).BuildAsync(EventId);
        var row = Assert.Single(
            rows, r => r.Job.FunctionName == JobWithUnwrittenHealthKey().FunctionName);

        Assert.Null(row.LastSuccessAt);
        Assert.Equal(0, row.ConsecutiveFailures);
    }

    /// <summary>
    /// The failure counter travels with the marker that was found — otherwise the page could show a
    /// success time from one row and a failure count from neither.
    /// </summary>
    [Fact]
    public async Task The_failure_counter_comes_from_the_same_marker_as_the_success_time()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        var job = JobWithUnwrittenHealthKey();
        db.JobHealthMarkers.Add(new JobHealthMarker
        {
            JobKey = job.FunctionName, LastSuccessAt = Now.AddMinutes(-30), ConsecutiveFailures = 3,
        });
        await db.SaveChangesAsync();

        var rows = await NewService(db).BuildAsync(EventId);
        var row = Assert.Single(rows, r => r.Job.FunctionName == job.FunctionName);

        Assert.Equal(Now.AddMinutes(-30), row.LastSuccessAt);
        Assert.Equal(3, row.ConsecutiveFailures);
    }

    /// <summary>
    /// 🔑 The catalog entries this was found on. Named so a future reader can see the shape rather
    /// than rediscovering it: a HealthKey equal to the FEATURE key, with no service writing it.
    /// </summary>
    [Fact]
    public void The_invoicing_jobs_are_the_shape_this_fix_exists_for()
    {
        foreach (var name in new[] { "WebshopInvoiceJob", "CouponInvoiceJob" })
        {
            var job = Assert.Single(JobCatalog.All, j => j.FunctionName == name);
            Assert.Equal(job.FeatureKey, job.HealthKey);
            Assert.NotEqual(job.FunctionName, job.HealthKey);
        }
    }
}
