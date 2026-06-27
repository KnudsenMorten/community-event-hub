using CommunityHub.Core.Data;
using CommunityHub.Core.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests the "alert only on 2 consecutive failures" gate (REQUIREMENTS §138, the
/// 2026-06-27 ErpWebshopReconcile 503 incident: "only ALERT if it fails twice in a
/// row — a single failure is likely a backup/platform glitch"). The first failure is
/// recorded but NOT alerted; a second consecutive failure alerts; a success resets the
/// counter so the next failure starts over at 1.
/// </summary>
public sealed class JobFailureTrackerTests
{
    private const string JobKey = "erp-webshop-reconcile";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"jobfail-{Guid.NewGuid():N}")
            .Options);

    private static JobFailureTracker NewTracker(CommunityHubDbContext db) =>
        new(db, TimeProvider.System, NullLogger<JobFailureTracker>.Instance);

    [Fact]
    public async Task First_failure_is_suppressed_second_consecutive_alerts()
    {
        using var db = NewDb();
        var tracker = NewTracker(db);

        var first = await tracker.RecordFailureAsync(JobKey, "503 from CM");
        Assert.Equal(1, first.ConsecutiveFailures);
        Assert.False(first.ShouldAlert); // single glitch — suppressed

        var second = await tracker.RecordFailureAsync(JobKey, "503 from CM again");
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.True(second.ShouldAlert); // two in a row — page the operator

        // The failure is persisted for observability even while suppressed.
        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == JobKey);
        Assert.Equal(2, marker.ConsecutiveFailures);
        Assert.Equal("503 from CM again", marker.LastError);
        Assert.NotNull(marker.LastFailureAt);
    }

    [Fact]
    public async Task Success_resets_the_counter()
    {
        using var db = NewDb();
        var tracker = NewTracker(db);

        await tracker.RecordFailureAsync(JobKey, "boom"); // 1
        await tracker.RecordSuccessAsync(JobKey);          // reset

        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == JobKey);
        Assert.Equal(0, marker.ConsecutiveFailures);
        Assert.NotNull(marker.LastSuccessAt);

        // The NEXT failure starts over at 1 and is suppressed again (not alerted).
        var afterReset = await tracker.RecordFailureAsync(JobKey, "boom again");
        Assert.Equal(1, afterReset.ConsecutiveFailures);
        Assert.False(afterReset.ShouldAlert);
    }

    [Fact]
    public async Task Single_failure_then_success_never_alerts()
    {
        using var db = NewDb();
        var tracker = NewTracker(db);

        var only = await tracker.RecordFailureAsync(JobKey, "transient");
        Assert.False(only.ShouldAlert);
        await tracker.RecordSuccessAsync(JobKey);

        // No alert was ever warranted across the run pair — exactly the incident's intent.
        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == JobKey);
        Assert.Equal(0, marker.ConsecutiveFailures);
    }

    [Fact]
    public async Task State_is_keyed_per_job_and_survives_a_fresh_tracker_instance()
    {
        using var db = NewDb();

        // Two genuinely back-to-back failures across DIFFERENT tracker instances
        // (mirrors two 30-min ticks across a process restart) still alert on the 2nd.
        var firstRun = await NewTracker(db).RecordFailureAsync(JobKey, "tick 1");
        Assert.False(firstRun.ShouldAlert);

        var secondRun = await NewTracker(db).RecordFailureAsync(JobKey, "tick 2");
        Assert.True(secondRun.ShouldAlert);

        // A different job's counter is independent.
        var other = await NewTracker(db).RecordFailureAsync("some-other-job", "unrelated");
        Assert.Equal(1, other.ConsecutiveFailures);
        Assert.False(other.ShouldAlert);
    }
}
