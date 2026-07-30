namespace CommunityHub.Core.Settings;

/// <summary>
/// §510 — the decision "should this tick actually run the job?", kept as pure arithmetic so it
/// can be tested. The enforcement point is <c>JobsPauseMiddleware</c> (the one place every timer
/// job already passes through), but the middleware needs a <c>FunctionContext</c> and a database,
/// so the rule itself lives here where a test can reach it.
/// </summary>
public static class JobThrottle
{
    /// <summary>
    /// §510 — the tolerance that stops a job drifting a whole base tick slower than asked.
    ///
    /// <para><b>Why this is needed at all.</b> <c>LastRunAt</c> is stamped a fraction of a second
    /// AFTER the tick that set it — the middleware does three database round-trips first. The next
    /// tick arrives on the cron's exact clock boundary, so the elapsed time it measures is a hair
    /// UNDER the interval (29:59.x against 30:00). Compared exactly, that tick is skipped and the
    /// job runs on the following one instead: "every 30 minutes" silently becomes every 35, and
    /// flaps between the two as host jitter moves the stamp around.</para>
    ///
    /// <para><b>Why 30 seconds is safe.</b> It must exceed the stamping delay (milliseconds
    /// normally, seconds on a cold instance) and stay well under
    /// <see cref="JobDescriptor.BaseTickMinutes"/>. Since consecutive ticks are a full base tick
    /// apart and no interval may be smaller than one, <c>interval - 30s</c> is still longer than
    /// the gap between any two ticks — so the grace can never let a job run twice in a row.</para>
    /// </summary>
    public static readonly TimeSpan TickGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// §510 — the cadence actually in force: the operator's value when he has set one, otherwise
    /// the catalog default. 0 means "no interval rule at all — run on every tick the cron offers".
    ///
    /// <para>The fallback is load-bearing. An interval-driven job's cron is only a fast base tick,
    /// so if an unset row (0) were read as "no limit" the job would run <b>six times more often</b>
    /// than it did before it was converted — against the ERP and Company Manager APIs.</para>
    /// </summary>
    public static int EffectiveIntervalMinutes(JobDescriptor? descriptor, int operatorMinutes)
        => operatorMinutes > 0 ? operatorMinutes : descriptor?.DefaultIntervalMinutes ?? 0;

    /// <summary>
    /// True when this tick must be skipped because the job ran too recently.
    /// A job that has never run is never skipped, so a newly-converted job starts immediately.
    /// </summary>
    public static bool ShouldSkip(int effectiveIntervalMinutes, DateTimeOffset? lastRunAt, DateTimeOffset now)
    {
        if (effectiveIntervalMinutes <= 0 || lastRunAt is null) return false;

        var due = TimeSpan.FromMinutes(effectiveIntervalMinutes) - TickGrace;
        return now - lastRunAt.Value < due;
    }
}
