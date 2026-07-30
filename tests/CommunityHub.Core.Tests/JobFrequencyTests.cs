using CommunityHub.Core.Settings;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §510 — the operator must be able to DEFINE a job's frequency from the Jobs page
/// ("i msut be able to define frequency"). The mechanism is a fast base-tick cron plus a
/// DB-backed interval, so the arithmetic in <see cref="JobThrottle"/> IS the feature: get it
/// wrong and the job runs at a cadence he did not ask for, with nothing on screen to say so.
///
/// <para>The pilot shipped without these tests and had three defects. Each one below is a
/// regression test for a real failure, not a hypothetical.</para>
/// </summary>
public sealed class JobFrequencyTests
{
    private static readonly DateTimeOffset Tick =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private static JobDescriptor Interval(int minutes) =>
        new("TestJob", "Test job", $"Every {minutes} minutes", "0 */5 * * * *",
            "Does a thing.", DefaultIntervalMinutes: minutes);

    private static JobDescriptor ClockAnchored() =>
        new("DailyJob", "Daily job", "Daily 07:20 UTC", "0 20 7 * * *", "Does a thing daily.");

    // ---------- which cadence applies ----------

    [Fact]
    public void An_unset_interval_falls_back_to_the_catalog_default()
    {
        // Load-bearing: an interval job's cron is only a 5-minute base tick, so reading an unset
        // row (0) as "no limit" would run it 6x more often than before it was converted —
        // against the ERP and Company Manager APIs.
        Assert.Equal(30, JobThrottle.EffectiveIntervalMinutes(Interval(30), operatorMinutes: 0));
    }

    [Fact]
    public void The_operators_value_beats_the_catalog_default()
    {
        Assert.Equal(10, JobThrottle.EffectiveIntervalMinutes(Interval(30), operatorMinutes: 10));
    }

    [Fact]
    public void A_clock_anchored_job_with_no_override_has_no_interval_rule_at_all()
    {
        // Its cron IS its schedule; the middleware must not second-guess it.
        Assert.Equal(0, JobThrottle.EffectiveIntervalMinutes(ClockAnchored(), operatorMinutes: 0));
    }

    [Fact]
    public void A_clock_anchored_job_can_still_be_throttled_the_old_327_way()
    {
        // §327 predates §510 and still applies: an operator may slow a clock-anchored job down.
        Assert.Equal(120, JobThrottle.EffectiveIntervalMinutes(ClockAnchored(), operatorMinutes: 120));
    }

    [Fact]
    public void An_unknown_function_has_no_interval_rule()
    {
        Assert.Equal(0, JobThrottle.EffectiveIntervalMinutes(descriptor: null, operatorMinutes: 0));
    }

    // ---------- when a tick actually runs ----------

    [Fact]
    public void A_job_that_has_never_run_runs_on_its_very_first_tick()
    {
        Assert.False(JobThrottle.ShouldSkip(30, lastRunAt: null, now: Tick));
    }

    [Fact]
    public void A_tick_well_inside_the_interval_is_skipped()
    {
        Assert.True(JobThrottle.ShouldSkip(30, Tick, Tick.AddMinutes(20)));
    }

    [Fact]
    public void A_tick_well_past_the_interval_runs()
    {
        Assert.False(JobThrottle.ShouldSkip(30, Tick, Tick.AddMinutes(35)));
    }

    [Fact]
    public void An_interval_of_zero_never_skips()
    {
        Assert.False(JobThrottle.ShouldSkip(0, Tick, Tick.AddSeconds(1)));
    }

    // ---------- the drift regression ----------

    [Fact]
    public void The_tick_at_exactly_the_interval_runs_even_though_the_stamp_lags_it()
    {
        // THE BUG: LastRunAt is stamped a fraction of a second AFTER the tick that set it (three
        // DB round-trips happen first), while the next tick lands on the cron's exact clock
        // boundary. Compared exactly, that tick measures 29:59.8 — a hair short — and was skipped,
        // so "every 30 minutes" silently ran every 35.
        var lastRun = Tick.AddMilliseconds(200);
        var nextTick = Tick.AddMinutes(30);

        Assert.True(nextTick - lastRun < TimeSpan.FromMinutes(30), "precondition: the gap IS short");
        Assert.False(JobThrottle.ShouldSkip(30, lastRun, nextTick));
    }

    [Fact]
    public void A_slow_stamp_of_several_seconds_still_does_not_cost_a_whole_tick()
    {
        // A cold instance can take seconds to reach the stamp. The grace must absorb that too.
        Assert.False(JobThrottle.ShouldSkip(30, Tick.AddSeconds(12), Tick.AddMinutes(30)));
    }

    [Fact]
    public void The_grace_can_never_let_two_consecutive_ticks_both_run()
    {
        // The safety property that makes the grace sound: ticks are a whole base tick apart, so
        // even the tightest interval leaves the following tick short by far more than the grace.
        var oneTickLater = Tick.AddMinutes(JobDescriptor.BaseTickMinutes);

        Assert.True(JobThrottle.ShouldSkip(10, Tick, oneTickLater));
        Assert.True(JobThrottle.ShouldSkip(30, Tick, oneTickLater));
    }

    [Fact]
    public void An_interval_equal_to_the_base_tick_runs_on_every_tick()
    {
        // "Every 5 minutes" must mean every tick, not every other one.
        Assert.False(JobThrottle.ShouldSkip(
            JobDescriptor.BaseTickMinutes,
            Tick.AddMilliseconds(200),
            Tick.AddMinutes(JobDescriptor.BaseTickMinutes)));
    }

    [Fact]
    public void The_grace_is_far_below_the_base_tick_so_the_safety_argument_holds()
    {
        Assert.True(JobThrottle.TickGrace < TimeSpan.FromMinutes(JobDescriptor.BaseTickMinutes) / 2,
            "The grace must stay well under half a base tick, or two ticks could both run.");
        Assert.True(JobThrottle.TickGrace >= TimeSpan.FromSeconds(10),
            "The grace must comfortably exceed how long the middleware takes to stamp LastRunAt.");
    }

    // ---------- catalog invariants: guard the conversion of the remaining jobs ----------

    [Fact]
    public void Every_interval_driven_job_is_bound_to_the_base_tick_cron()
    {
        // Converting a job means giving it the fast base tick. Leave its old bespoke cron in place
        // and "Runs every 10" would be a promise the schedule cannot keep.
        var expected = $"0 */{JobDescriptor.BaseTickMinutes} * * * *";
        var wrong = JobCatalog.All
            .Where(j => j.IsIntervalDriven && !string.Equals(j.Cron, expected, StringComparison.Ordinal))
            .Select(j => $"{j.FunctionName}: '{j.Cron}'")
            .ToList();

        Assert.True(wrong.Count == 0,
            $"An interval-driven job must be bound to the base tick '{expected}', or the operator's "
            + "chosen frequency cannot be honoured: " + string.Join(" | ", wrong));
    }

    [Fact]
    public void Every_interval_driven_default_is_a_whole_multiple_of_the_base_tick()
    {
        // A default of 7 against a 5-minute tick would really run every 10 — the page would state
        // a cadence the system cannot produce.
        var bad = JobCatalog.All
            .Where(j => j.IsIntervalDriven
                        && j.DefaultIntervalMinutes!.Value % JobDescriptor.BaseTickMinutes != 0)
            .Select(j => $"{j.FunctionName}: {j.DefaultIntervalMinutes}")
            .ToList();

        Assert.True(bad.Count == 0,
            $"Defaults must be a multiple of the {JobDescriptor.BaseTickMinutes}-minute base tick: "
            + string.Join(" | ", bad));
    }

    [Fact]
    public void No_interval_driven_default_is_below_the_floor()
    {
        var tooFast = JobCatalog.All
            .Where(j => j.IsIntervalDriven
                        && j.DefaultIntervalMinutes!.Value < JobDescriptor.BaseTickMinutes)
            .Select(j => j.FunctionName)
            .ToList();

        Assert.True(tooFast.Count == 0,
            "Nothing can run faster than the base tick: " + string.Join(", ", tooFast));
    }

    [Fact]
    public void Clock_anchored_jobs_are_not_converted()
    {
        // §510's hard constraint: "every day at 07:20 UTC" anchored to a clock time must NOT
        // become tick+interval, because the send would drift off the hour. A job whose cron names
        // a specific minute or hour is clock-anchored by definition.
        var drifted = JobCatalog.All
            .Where(j => j.IsIntervalDriven)
            .Where(j =>
            {
                var fields = j.Cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // second minute hour ... — anything pinned in the hour field is clock-anchored.
                return fields.Length >= 3 && fields[2] != "*";
            })
            .Select(j => $"{j.FunctionName}: '{j.Cron}'")
            .ToList();

        Assert.True(drifted.Count == 0,
            "A clock-anchored job must keep its cron and hide the frequency box, not be converted "
            + "to an interval: " + string.Join(" | ", drifted));
    }

    [Fact]
    public void The_erp_customer_and_contact_sync_runs_every_ten_minutes()
    {
        // §510 converted this job to an interval-driven cadence (a 5-minute base TICK plus
        // DefaultIntervalMinutes) and deliberately kept 30 so the conversion changed no behaviour.
        //
        // §595 — the operator has now chosen otherwise: *"it must run every 10 min"*. This is the
        // job he was looking for on /Organizer/Jobs: the ERP (e-conomic) CUSTOMER + CONTACT sync
        // into Company Manager / the webshop. He explicitly distinguished it from the webshop ORDER
        // pull — *"but the webshop pull is different job … there is a different job that handles
        // the customer + contact sync from erp"* — so WooCommercePullJob must NOT move with it.
        var erp = JobCatalog.Find("ErpSyncCustomerContactJob");
        Assert.NotNull(erp);
        Assert.True(erp!.IsIntervalDriven);
        Assert.Equal(10, erp.DefaultIntervalMinutes);

        // The pin that keeps the two jobs from being conflated again.
        var pull = JobCatalog.Find("WooCommercePullJob");
        Assert.NotNull(pull);
        Assert.Equal("0 */15 * * * *", pull!.Cron);
    }

    // ---------- what the Jobs page states ----------

    private static JobStatusRow Row(JobDescriptor job, int minInterval) =>
        new(job, minInterval, LastRunAt: Tick, LastThrottledAt: null, ThrottledCount: 0,
            FeatureEnabled: true, LastSuccessAt: Tick, ConsecutiveFailures: 0);

    [Fact]
    public void An_interval_job_the_operator_has_not_touched_displays_its_default()
    {
        // The Schedule column states this as the cadence, so it must never read 0 or the base tick.
        Assert.Equal(30, Row(Interval(30), minInterval: 0).EffectiveIntervalMinutes);
    }

    [Fact]
    public void An_interval_job_displays_the_operators_chosen_frequency()
    {
        Assert.Equal(10, Row(Interval(30), minInterval: 10).EffectiveIntervalMinutes);
    }

    [Fact]
    public void An_interval_job_is_never_badged_LIMITED()
    {
        // The badge means "held back below its natural cadence". On a converted job a value IS the
        // cadence, so every such job would have worn a warning that means the opposite of the truth.
        Assert.False(Row(Interval(30), minInterval: 10).IsThrottled);
        Assert.False(Row(Interval(30), minInterval: 0).IsThrottled);
    }

    [Fact]
    public void A_clock_anchored_job_is_still_badged_LIMITED_when_it_is_held_back()
    {
        // §327 behaviour must survive §510 — that badge is the only sign the job is being skipped.
        Assert.True(Row(ClockAnchored(), minInterval: 120).IsThrottled);
        Assert.False(Row(ClockAnchored(), minInterval: 0).IsThrottled);
    }
}
