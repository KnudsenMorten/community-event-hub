using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §545(a) — the Jobs page must be able to SHOW "green and doing nothing", the state it could not
/// distinguish from a healthy run.
/// </summary>
/// <remarks>
/// <para><b>The incident this row format failed at (§544).</b> The session push was switched off by
/// a setting. The Health cell read <i>"OK, 4 minutes ago"</i> every single time, for weeks, because
/// the job really was succeeding — at doing nothing. It ended with 1,500 attendees and no agenda.
/// §545: <i>"the gap is not detection, it is surfacing"</i>.</para>
///
/// <para>This is the SURFACING half of §545(b): §627 made the state countable, and this makes it
/// visible on the page he already opens — no Azure access required, which was the whole ask.</para>
/// </remarks>
public class JobsPageInactiveBadgeTests
{
    private static JobStatusRow Row(int noOps, string? reason, int failures = 0) =>
        new(JobCatalog.All[0], 0, DateTimeOffset.UtcNow, null, 0, true,
            DateTimeOffset.UtcNow, failures, noOps, reason);

    [Fact]
    public void A_job_doing_nothing_run_after_run_is_badged()
    {
        var row = Row(30, "The sync direction excludes it.");

        Assert.True(row.IsInactive);
        Assert.False(row.IsUnhealthy);   // it is NOT failing — that is exactly why it was invisible
    }

    /// <summary>
    /// 🔒 A single doing-nothing run is ORDINARY — nothing had changed since the last pass. Badging
    /// it would put a warning on almost every row and mean nothing, which is the §609 mistake.
    /// </summary>
    [Fact]
    public void One_or_two_quiet_runs_are_NOT_badged()
    {
        Assert.False(Row(1, "Nothing to do.").IsInactive);
        Assert.False(Row(2, "Nothing to do.").IsInactive);
    }

    [Fact]
    public void A_job_that_has_never_reported_activity_is_NOT_badged()
    {
        // Silence is UNKNOWN. An un-instrumented job must look exactly as it did before §627 —
        // this is what makes rolling the instrumentation out job by job safe.
        Assert.False(Row(0, null).IsInactive);
    }

    /// <summary>
    /// The page badge speaks up far earlier than the mail. He only sees a badge when he chooses to
    /// look, so it can afford to be chatty; an uninvited mail cannot (§609).
    /// </summary>
    [Fact]
    public void The_page_badge_appears_long_before_the_ALERT_fires()
    {
        Assert.True(JobStatusRow.InactiveBadgeThreshold
                    < CommunityHub.Core.Diagnostics.EngineFailureAlertGate.ConsecutiveNoOpAlertThreshold);
    }

    [Fact]
    public void A_FAILING_job_still_reads_as_failing_first()
    {
        // Failure is the more urgent story, and the page's else-if order must keep it on top.
        var row = Row(30, "The sync direction excludes it.", failures: 3);

        Assert.True(row.IsUnhealthy);
    }
}
