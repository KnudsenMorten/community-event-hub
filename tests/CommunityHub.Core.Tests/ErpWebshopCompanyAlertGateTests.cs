using CommunityHub.Core.Data;
using CommunityHub.Core.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §921 — A COMPANY THAT FAILS ONCE IS NOT AN E-MAIL. Escalated NINE times.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"if for some reason an api is down, I dont want to be boughtered
/// with it when it happend 1 time, 2 time, 3 times, after 1 hour of issues like 6 x 10 min, it is ok
/// to get notification … i have said it now 9 times!"</i></para>
///
/// <para>🔴 <b>Why the earlier fixes did not take.</b> §138 set the gate at 2 consecutive failures;
/// §784.4 raised it to 3 after he complained. Both changed
/// <see cref="JobFailureTracker"/>, which is consulted <b>when a JOB THROWS</b> — and the ERP
/// reconcile deliberately CATCHES the per-company exception so one bad company cannot stop the fleet
/// reconciling. The job therefore never threw, the gate was never consulted, and the note went
/// straight into the alert every single time. <b>The right idea, applied twice to the wrong code
/// path.</b></para>
///
/// <para>🔒 This test pins the gate on the path that actually produces his e-mail — keyed PER
/// COMPANY, at the threshold that matches his sentence: six consecutive runs of a ten-minute
/// reconcile is the hour he asked for.</para>
/// </remarks>
public sealed class ErpWebshopCompanyAlertGateTests
{
    /// <summary>The key shape the reconcile uses, per company.</summary>
    private const string Key = "erp-webshop-cm:341409319";

    /// <summary>Mirrors ErpWebshopContactSyncService.CompanyFailureAlertThreshold.</summary>
    private const int Threshold = 6;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"erp-gate-{Guid.NewGuid():N}").Options);

    private static JobFailureTracker NewTracker(CommunityHubDbContext db) =>
        new(db, TimeProvider.System, NullLogger<JobFailureTracker>.Instance);

    /// <summary>
    /// 🔴 The exact complaint: a 503 on runs 1–5 is silent; the sixth — an hour in — speaks.
    /// </summary>
    [Fact]
    public async Task The_first_five_failures_are_silent_and_the_sixth_reports()
    {
        using var db = NewDb();
        var tracker = NewTracker(db);

        for (var run = 1; run <= Threshold - 1; run++)
        {
            var d = await tracker.RecordFailureAsync(
                Key, "503 (Service Unavailable)", default, Threshold);

            Assert.Equal(run, d.ConsecutiveFailures);
            Assert.False(d.ShouldAlert, $"run {run} alerted — he asked not to be told before an hour");
        }

        var sixth = await tracker.RecordFailureAsync(
            Key, "503 (Service Unavailable)", default, Threshold);

        Assert.Equal(Threshold, sixth.ConsecutiveFailures);
        Assert.True(sixth.ShouldAlert, "an hour of continuous failure SHOULD be reported");
    }

    /// <summary>
    /// 🔒 A company that reconciles clears its streak — otherwise the counter creeps across
    /// unrelated blips weeks apart and eventually alerts on a healthy company: an alert with no
    /// incident behind it, which is the same deafness by a slower route.
    /// </summary>
    [Fact]
    public async Task A_successful_run_clears_the_streak()
    {
        using var db = NewDb();
        var tracker = NewTracker(db);

        for (var i = 0; i < Threshold - 1; i++)
        {
            await tracker.RecordFailureAsync(Key, "503", default, Threshold);
        }

        await tracker.RecordSuccessAsync(Key);

        var afterRecovery = await tracker.RecordFailureAsync(Key, "503", default, Threshold);

        Assert.Equal(1, afterRecovery.ConsecutiveFailures);
        Assert.False(afterRecovery.ShouldAlert);
    }

    /// <summary>
    /// ⚠️ The streak is PER COMPANY. One company being down must not push another over the
    /// threshold — nor hide a second company that is genuinely failing.
    /// </summary>
    [Fact]
    public async Task Two_companies_keep_separate_streaks()
    {
        using var db = NewDb();
        var tracker = NewTracker(db);

        for (var i = 0; i < Threshold; i++)
        {
            await tracker.RecordFailureAsync("erp-webshop-cm:111", "503", default, Threshold);
        }

        var other = await tracker.RecordFailureAsync("erp-webshop-cm:222", "503", default, Threshold);

        Assert.Equal(1, other.ConsecutiveFailures);
        Assert.False(other.ShouldAlert);
    }

    /// <summary>
    /// 🔒 The state is PERSISTED, so the streak survives a restart or a redeploy between ticks —
    /// otherwise every deploy would reset the count and an outage spanning one would never report.
    /// </summary>
    [Fact]
    public async Task The_streak_survives_a_new_tracker_instance()
    {
        using var db = NewDb();

        for (var i = 0; i < Threshold - 1; i++)
        {
            await NewTracker(db).RecordFailureAsync(Key, "503", default, Threshold);
        }

        // A fresh instance, as a later tick (or a redeployed process) would create.
        var sixth = await NewTracker(db).RecordFailureAsync(Key, "503", default, Threshold);

        Assert.Equal(Threshold, sixth.ConsecutiveFailures);
        Assert.True(sixth.ShouldAlert);
    }
}
