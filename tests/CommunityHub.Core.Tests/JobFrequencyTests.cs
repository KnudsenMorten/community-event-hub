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
        var oneTickLater = Tick.AddMinutes(JobDescriptor.DefaultTickMinutes);

        Assert.True(JobThrottle.ShouldSkip(10, Tick, oneTickLater));
        Assert.True(JobThrottle.ShouldSkip(30, Tick, oneTickLater));
    }

    [Fact]
    public void An_interval_equal_to_the_base_tick_runs_on_every_tick()
    {
        // "Every 5 minutes" must mean every tick, not every other one.
        Assert.False(JobThrottle.ShouldSkip(
            JobDescriptor.DefaultTickMinutes,
            Tick.AddMilliseconds(200),
            Tick.AddMinutes(JobDescriptor.DefaultTickMinutes)));
    }

    [Fact]
    public void The_grace_is_far_below_the_base_tick_so_the_safety_argument_holds()
    {
        Assert.True(JobThrottle.TickGrace < TimeSpan.FromMinutes(JobDescriptor.DefaultTickMinutes) / 2,
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
        // §878 — the tick is PER JOB now (1 minute for the webhook drain, 5 for everything else),
        // so each job is checked against ITS OWN tick rather than a global constant.
        var wrong = JobCatalog.All
            .Where(j => j.IsIntervalDriven)
            .Where(j => !string.Equals(j.Cron, Expected(j), StringComparison.Ordinal))
            .Select(j => $"{j.FunctionName}: '{j.Cron}' (expected '{Expected(j)}')")
            .ToList();

        Assert.True(wrong.Count == 0,
            "An interval-driven job must be bound to its own base tick, or the operator's "
            + "chosen frequency cannot be honoured: " + string.Join(" | ", wrong));

        static string Expected(JobDescriptor j) =>
            j.BaseTickMinutes == 1 ? "0 * * * * *" : $"0 */{j.BaseTickMinutes} * * * *";
    }

    [Fact]
    public void Every_interval_driven_default_is_a_whole_multiple_of_the_base_tick()
    {
        // A default of 7 against a 5-minute tick would really run every 10 — the page would state
        // a cadence the system cannot produce.
        var bad = JobCatalog.All
            .Where(j => j.IsIntervalDriven
                        && j.DefaultIntervalMinutes!.Value % j.BaseTickMinutes != 0)
            .Select(j => $"{j.FunctionName}: {j.DefaultIntervalMinutes} (tick {j.BaseTickMinutes})")
            .ToList();

        Assert.True(bad.Count == 0,
            "Defaults must be a whole multiple of the job's own base tick: "
            + string.Join(" | ", bad));
    }

    [Fact]
    public void No_interval_driven_default_is_below_the_floor()
    {
        var tooFast = JobCatalog.All
            .Where(j => j.IsIntervalDriven
                        && j.DefaultIntervalMinutes!.Value < j.BaseTickMinutes)
            .Select(j => j.FunctionName)
            .ToList();

        Assert.True(tooFast.Count == 0,
            "Nothing can run faster than the base tick: " + string.Join(", ", tooFast));
    }

    [Fact]
    public void EVERY_job_in_the_catalog_is_operator_settable()
    {
        // 🔒 §878 — THIS TEST REPLACED ITS OWN OPPOSITE, AND THAT IS THE POINT.
        //
        // It used to be `Clock_anchored_jobs_are_not_converted`, asserting that a job whose cron
        // pinned an hour must KEEP that cron and hide the frequency box. That was §510's rule and
        // it was not wrong when written — the operator superseded it.
        //
        // He asked four times for every job to be re-timeable, and I kept delivering a two-class
        // page: some rows with a box, others with a sentence explaining why they had none. His
        // screenshot marked the one row without a box WRONG and the four boxed rows CORRECT:
        // *"i asked for a feature to explicitely set a frequency (minute) for every job … make all
        // background jobs consistent"*. A page that argues with itself about which jobs you may
        // control IS the inconsistency he was reporting.
        //
        // ⚠️ The cost is accepted and recorded (§878.4): a daily job on an interval is spaced from
        // its LAST RUN, so its time of day creeps. If that ever matters the fix is to anchor the
        // daily ones to a stored time-of-day — NOT to bring back a row with no control on it.
        var unsettable = JobCatalog.All
            .Where(j => !j.IsIntervalDriven)
            .Select(j => $"{j.FunctionName} ('{j.Cron}')")
            .ToList();

        Assert.True(unsettable.Count == 0,
            "Every job must expose a frequency the operator can set. These have no box, which is "
            + "the inconsistency §878 removed: " + string.Join(" | ", unsettable));
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
        //
        // 🔒 §869.3 — THIS PIN NOW ASSERTS THE CADENCE, NOT THE CRON. It used to read
        // `Assert.Equal("0 */15 * * * *", pull.Cron)`, which was a fair proxy while the webshop
        // pull was cron-driven: its cron WAS its cadence. §869.3 converted it (he asked three times
        // for every job to be re-timeable from the page), so the cron is now the shared 5-minute
        // base tick and the 15 minutes live in DefaultIntervalMinutes.
        //
        // ⚠️ So the proxy would have failed while the invariant it protects was perfectly intact.
        // Assert the invariant DIRECTLY instead — the webshop ORDER pull did not move to the ERP
        // sync's 10 minutes. That is what §595 actually bought, and the next conversion cannot
        // break this form of it by accident.
        // ⚰️ §878.5 — THE "THEY MUST DIFFER" HALF OF THIS PIN IS RETIRED, DELIBERATELY.
        // §595 kept the webshop pull at 15 while the ERP sync moved to 10, so the pin asserted they
        // were NOT equal. Operator 2026-08-05: *"default for all are every 10 min"* — he has now
        // set ONE default across the whole catalog, so both are 10 and the difference he was
        // protecting no longer exists as a default. What §595 actually bought — that these are two
        // DIFFERENT JOBS he can time independently — survives, and is now enforced by the page
        // itself: each has its own box. Asserting inequality here would fail on a value he chose.
        var pull = JobCatalog.Find("WooCommercePullJob");
        Assert.NotNull(pull);
        Assert.True(pull!.IsIntervalDriven);
        Assert.NotSame(erp, pull);
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
