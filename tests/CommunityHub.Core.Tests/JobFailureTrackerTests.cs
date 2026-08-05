using CommunityHub.Core.Data;
using CommunityHub.Core.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The "alert only on N consecutive failures" gate.
/// </summary>
/// <remarks>
/// <para>🔴 <b>§784.4 — N IS NOW 3, not 2 (operator 2026-08-03).</b> <i>"I told you to NOT send these
/// when they happen first time, but only alert when it has happened for 3 consequtive times. this is
/// caused by a temporary hiccup in the integration. you are warning of something which we cannto do
/// anything about (503 service unavailable)"</i>.</para>
///
/// <para>§138 set it to 2 after the 2026-06-27 ErpWebshopReconcile incident. That proved too tight:
/// an upstream 503 routinely spans two consecutive ticks, so the gate fired on precisely the class of
/// blip it was built to absorb — and he kept receiving mail about someone else's server.</para>
///
/// <para>🔑 The counter still records EVERY failure for observability while suppressed; the threshold
/// only decides whether to PAGE. Alerting too early does not cost noise, it costs deafness.</para>
/// </remarks>
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
    public async Task The_first_TWO_failures_are_suppressed_and_the_third_alerts()
    {
        using var db = NewDb();
        var tracker = NewTracker(db);

        var first = await tracker.RecordFailureAsync(JobKey, "503 from CM");
        Assert.Equal(1, first.ConsecutiveFailures);
        Assert.False(first.ShouldAlert);   // single glitch — suppressed

        // 🔴 §784.4 — THIS is the line that changed. Under the old threshold of 2 the operator was
        // paged here, for a 503 spanning two ticks that he could do nothing about.
        var second = await tracker.RecordFailureAsync(JobKey, "503 from CM again");
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.False(second.ShouldAlert);  // STILL a hiccup — still silent

        var third = await tracker.RecordFailureAsync(JobKey, "503 from CM a third time");
        Assert.Equal(3, third.ConsecutiveFailures);
        Assert.True(third.ShouldAlert);    // three in a row — no longer a blip

        // The failure is persisted for observability even while suppressed.
        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == JobKey);
        Assert.Equal(3, marker.ConsecutiveFailures);
        Assert.Equal("503 from CM a third time", marker.LastError);
        Assert.NotNull(marker.LastFailureAt);
    }

    [Fact]
    public void The_shipped_threshold_is_what_the_operator_asked_for()
    {
        // Pinned as a VALUE, not only as behaviour: the number is the decision, and the next person
        // tempted to "tighten it up" should have to change a test that says whose call it was.
        Assert.Equal(3, JobFailureTracker.DefaultAlertThreshold);
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

        // Genuinely back-to-back failures across DIFFERENT tracker instances (mirroring successive
        // ticks across a process restart) still accumulate — the counter is DURABLE, which is the
        // point of this test. §784.4: it now takes THREE to alert.
        var firstRun = await NewTracker(db).RecordFailureAsync(JobKey, "tick 1");
        Assert.False(firstRun.ShouldAlert);

        var secondRun = await NewTracker(db).RecordFailureAsync(JobKey, "tick 2");
        Assert.Equal(2, secondRun.ConsecutiveFailures);
        Assert.False(secondRun.ShouldAlert);

        var thirdRun = await NewTracker(db).RecordFailureAsync(JobKey, "tick 3");
        Assert.Equal(3, thirdRun.ConsecutiveFailures);
        Assert.True(thirdRun.ShouldAlert);   // survived two restarts and still counted

        // A different job's counter is independent.
        var other = await NewTracker(db).RecordFailureAsync("some-other-job", "unrelated");
        Assert.Equal(1, other.ConsecutiveFailures);
        Assert.False(other.ShouldAlert);
    }
}
