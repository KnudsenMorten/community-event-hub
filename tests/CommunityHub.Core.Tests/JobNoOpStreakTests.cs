using CommunityHub.Core.Data;
using CommunityHub.Core.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §545(b) INACTIVE — the DURABLE no-op streak, the counter that makes "green and doing nothing"
/// an alertable state.
/// </summary>
/// <remarks>
/// <para><b>Why <see cref="JobFailureTracker"/> could never have caught §544.</b> It counts
/// CONSECUTIVE FAILURES, and the session push did not fail — it succeeded, every run, for weeks,
/// while a setting turned it away. Same story for §576 (a deleted sync stage still being demanded)
/// and §621 (a config flag never set). Three features inert, three green Jobs pages, zero alerts.</para>
///
/// <para>Durability is the point: the Flex-Consumption host scales to zero between ticks, so an
/// in-memory counter would reset and never reach a threshold — the same reason the failure counter
/// lives in a <c>JobHealthMarker</c> row.</para>
/// </remarks>
public sealed class JobNoOpStreakTests
{
    private const string JobKey = "SessionBackstagePushJob";
    private const string Gated = "Speaker push: the sync direction excludes it.";

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"noop-{Guid.NewGuid():N}")
            .Options);

    private static JobFailureTracker NewTracker(CommunityHubDbContext db) =>
        new(db, TimeProvider.System, NullLogger<JobFailureTracker>.Instance);

    [Fact]
    public async Task Consecutive_no_ops_ACCUMULATE_across_runs()
    {
        using var db = NewDb();
        var t = NewTracker(db);

        Assert.Equal(1, await t.RecordActivityAsync(JobKey, Gated));
        Assert.Equal(2, await t.RecordActivityAsync(JobKey, Gated));
        Assert.Equal(3, await t.RecordActivityAsync(JobKey, Gated));
    }

    [Fact]
    public async Task Doing_real_WORK_resets_the_streak_and_clears_the_reason()
    {
        using var db = NewDb();
        var t = NewTracker(db);

        await t.RecordActivityAsync(JobKey, Gated);
        await t.RecordActivityAsync(JobKey, Gated);

        Assert.Equal(0, await t.RecordActivityAsync(JobKey, inactiveReason: null));

        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == JobKey);
        Assert.Equal(0, marker.ConsecutiveNoOps);
        Assert.Null(marker.LastNoOpReason);
    }

    /// <summary>
    /// 🔒 "Feature off" for 40 runs and then "no active edition" are two different stories. Merging
    /// them would report a count against the wrong reason — and the reason is the whole payload of
    /// the alert, since it is what tells him whether to act.
    /// </summary>
    [Fact]
    public async Task A_DIFFERENT_reason_restarts_the_count()
    {
        using var db = NewDb();
        var t = NewTracker(db);

        await t.RecordActivityAsync(JobKey, Gated);
        await t.RecordActivityAsync(JobKey, Gated);
        await t.RecordActivityAsync(JobKey, Gated);

        Assert.Equal(1, await t.RecordActivityAsync(JobKey, "Zoho is switched off, so nothing is pushed."));
    }

    [Fact]
    public async Task The_reason_stored_is_the_one_the_operator_will_READ()
    {
        using var db = NewDb();
        var t = NewTracker(db);

        await t.RecordActivityAsync(JobKey, Gated);

        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == JobKey);
        Assert.Equal(Gated, marker.LastNoOpReason);
    }

    /// <summary>
    /// Activity bookkeeping must not disturb the FAILURE counter — they answer different questions
    /// and an inactive run is not a failing one.
    /// </summary>
    [Fact]
    public async Task Recording_activity_does_not_touch_the_consecutive_FAILURE_count()
    {
        using var db = NewDb();
        var t = NewTracker(db);

        await t.RecordFailureAsync(JobKey, "boom");
        await t.RecordActivityAsync(JobKey, Gated);

        var marker = await db.JobHealthMarkers.SingleAsync(m => m.JobKey == JobKey);
        Assert.Equal(1, marker.ConsecutiveFailures);
        Assert.Equal(1, marker.ConsecutiveNoOps);
    }

    /// <summary>
    /// The threshold is deliberately high. A doing-nothing run is usually CORRECT — a feature is off
    /// because he switched it off, and he does not need telling every 10 minutes (§609 is what
    /// happens when a channel cries wolf). At the sync jobs' 10-minute cadence this is ~17 hours:
    /// long enough that no deliberate same-day toggle trips it, short enough that "weeks" cannot
    /// happen again.
    /// </summary>
    [Fact]
    public void The_alert_threshold_is_generous_enough_not_to_cry_wolf()
    {
        Assert.Equal(100, EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold);

        var hoursAtTenMinuteCadence = EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold * 10 / 60.0;
        Assert.True(hoursAtTenMinuteCadence > 12,
            "A threshold under half a day would fire on an ordinary overnight toggle.");
        Assert.True(hoursAtTenMinuteCadence < 24 * 7,
            "§544 went unnoticed for WEEKS — the threshold must be well inside that.");
    }
}
