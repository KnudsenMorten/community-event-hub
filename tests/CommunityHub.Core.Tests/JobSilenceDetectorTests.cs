using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §545 — the silent-job watchdog. Operator: <i>"alerting for INACTIVE / SILENT / NO-OP /
/// CREDENTIAL, not just failures. Every incident today was visible in logs and invisible to him."</i>
///
/// <para><b>The gap it fills.</b> <see cref="JobFailureTracker"/> alerts on consecutive FAILURES.
/// On 2026-07-28 three features were inert and NONE of them failed: a deleted sync stage still being
/// demanded (§576), a config flag never set (§621), and a resource missing from the paging reject
/// list so every read came back empty (§585). A job that succeeds at doing nothing is invisible.</para>
///
/// <para><b>Restraint is half the design.</b> §609 was a red mail every run for a by-design
/// condition, and it trained him to distrust the channel. So these tests pin the NEGATIVE cases as
/// hard as the positive ones: a rare-cadence job is never flagged, and a job that is merely a little
/// late is never flagged.</para>
/// </summary>
public class JobSilenceDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 6, 40, 0, TimeSpan.Zero);

    private static IReadOnlyList<JobSilenceDetector.Silent> Eval(
        params (string Key, DateTimeOffset? Last)[] markers) =>
        JobSilenceDetector.Evaluate(
            markers.ToDictionary(m => m.Key, m => m.Last, StringComparer.OrdinalIgnoreCase), Now);

    // ---------- it FIRES on the states that were invisible ----------

    [Fact]
    public void A_frequent_job_that_has_not_succeeded_for_days_is_flagged()
    {
        // The §576 shape: running every 10 minutes, green, and nothing happening for weeks.
        var flagged = Eval(("SessionBackstagePushJob", Now.AddDays(-3)));

        var s = Assert.Single(flagged, f => f.JobKey == "SessionBackstagePushJob");
        Assert.Equal(JobSilenceDetector.SilenceKind.Stale, s.Kind);
        Assert.Contains("last succeeded", s.Detail);
    }

    [Fact]
    public void A_job_that_has_NEVER_succeeded_is_flagged()
    {
        // The §621 shape: the engine had never once looked, and nothing said so.
        var flagged = Eval(("SpeakerChangeDetectionJob", null));

        var s = Assert.Single(flagged, f => f.JobKey == "SpeakerChangeDetectionJob");
        Assert.Equal(JobSilenceDetector.SilenceKind.NeverSucceeded, s.Kind);
    }

    [Fact]
    public void A_marker_with_no_job_behind_it_is_flagged_as_orphaned()
    {
        // Tonight's own rename (§595 ErpWebshopReconcileJob → ErpSyncCustomerContactJob) left one
        // behind. An orphan quietly stops being watched, which is the same silence by another route.
        var flagged = Eval(("ErpWebshopReconcileJob", Now.AddMinutes(-5)));

        var s = Assert.Single(flagged, f => f.JobKey == "ErpWebshopReconcileJob");
        Assert.Equal(JobSilenceDetector.SilenceKind.OrphanedMarker, s.Kind);
        Assert.Contains("Nothing is watching it", s.Detail);
    }

    // ---------- 🔒 it STAYS QUIET where an alert would cry wolf ----------

    [Fact]
    public void A_healthy_job_is_not_flagged()
    {
        Assert.Empty(Eval(("SessionBackstagePushJob", Now.AddMinutes(-5))));
    }

    [Fact]
    public void A_job_that_is_merely_a_LITTLE_late_is_not_flagged()
    {
        // 10-minute cadence, 20 minutes late. Real schedulers jitter; alerting here would be noise.
        Assert.Empty(Eval(("SessionBackstagePushJob", Now.AddMinutes(-20))));
    }

    // ⚰️ §878 — `An_ANNUAL_placeholder_job_is_NEVER_flagged_however_old_it_is` lived here and used
    // SessionPushPilotJob, the catalog's only annual job. That job is RETIRED (operator: *"i have
    // no idea what this is doing … it should be deleted (not used)"*), so the test lost its
    // subject. The RULE it guarded is still live and still covered — `ExpectedGap` returns null for
    // an annual cron, asserted directly in `A_rare_cadence_job_has_no_expected_gap_at_all` below.

    [Fact]
    public void A_daily_job_that_ran_this_morning_is_not_flagged()
    {
        Assert.Empty(Eval(("AuditPurgeJob", Now.AddHours(-2))));
    }

    // ---------- the cadence maths the rules stand on ----------

    [Theory]
    [InlineData("ZohoWebhookDrainJob", 1)]        // "0 * * * * *"  → every minute
    [InlineData("AttendeeBackstageSyncJob", 10)]  // "0 */10 * * * *"
    [InlineData("AuditPurgeJob", 1440)]           // "0 0 4 * * *"  → daily
    public void Expected_gap_is_derived_from_the_cron(string fn, int expectedMinutes)
    {
        var job = JobCatalog.Find(fn)!;
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), JobSilenceDetector.ExpectedGap(job));
    }

    [Fact]
    public void An_interval_driven_job_uses_its_INTERVAL_not_its_base_tick()
    {
        // §510: the cron is only a 5-minute base tick for these; judging by it would flag a job that
        // is running exactly as configured.
        var job = JobCatalog.Find("SessionBackstagePushJob")!;
        Assert.True(job.IsIntervalDriven);
        Assert.Equal(TimeSpan.FromMinutes(job.DefaultIntervalMinutes!.Value),
            JobSilenceDetector.ExpectedGap(job));
    }

    [Fact]
    public void A_rare_cadence_job_has_no_expected_gap_at_all()
    {
        // §878 — asserted against a CONSTRUCTED annual descriptor now that the catalog has no
        // annual job left (SessionPushPilotJob retired). The rule outlives its last example: a job
        // pinned to a day AND month is never judged stale, or the watchdog cries wolf every year.
        var annual = new JobDescriptor(
            "AnnualPlaceholderJob", "Annual placeholder", "Manual only", "0 0 0 1 1 *",
            "Parked on an annual cron so it never fires on its own.");

        Assert.Null(JobSilenceDetector.ExpectedGap(annual));
    }

    // ---------- §707.27 E — an HTTP-triggered function is not an orphan ----------

    /// <summary>
    /// §707.27 E — <c>ZohoOrderWebhook</c> is HTTP-triggered, so having no <c>JobCatalog</c> row is
    /// CORRECT. It was reported as *"most likely left behind by a rename"* — a confident, specific
    /// and WRONG diagnosis, which is how a recurring alert teaches you to skim past a real orphan.
    ///
    /// <para>🔒 Fixed with an allow-list, NOT a <c>JobDescriptor</c>: <c>JobCatalogCompletenessTests</c>
    /// asserts the catalog equals the real <c>[TimerTrigger]</c> set, and that guarantee is worth more
    /// than the convenience of silencing this one line.</para>
    /// </summary>
    [Fact]
    public void A_known_HTTP_triggered_function_is_not_reported_as_an_orphan()
    {
        Assert.Empty(Eval(("ZohoOrderWebhook", Now.AddMinutes(-3))));
    }

    /// <summary>
    /// An HTTP function has no cadence, so it can never be judged STALE either — it fires when an
    /// external system calls it, and a quiet month is not evidence of a fault.
    /// (The narrowness of the allow-list is pinned by
    /// <see cref="A_marker_with_no_job_behind_it_is_flagged_as_orphaned"/>, which still fires.)
    /// </summary>
    [Fact]
    public void A_known_HTTP_triggered_function_is_never_flagged_as_stale()
    {
        Assert.Empty(Eval(("ZohoOrderWebhook", Now.AddDays(-30))));
    }
}
