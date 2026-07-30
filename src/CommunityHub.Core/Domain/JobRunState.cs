namespace CommunityHub.Core.Domain;

/// <summary>
/// §327 — the operator-editable RUN STATE of one background job: how often it is allowed to
/// do work, and when it last did.
///
/// <para><b>Why this exists.</b> Azure Functions binds a <c>[TimerTrigger]</c> cron when the
/// host STARTS, so nothing written at runtime can change a shipped cadence. What an operator
/// actually needs mid-event is the ability to say "this is too chatty, dial it down NOW"
/// without a deploy. <see cref="MinIntervalMinutes"/> does exactly that: the job still
/// TRIGGERS on its cron, but <c>JobsPauseMiddleware</c> skips the invocation unless enough
/// time has passed since <see cref="LastRunAt"/>.</para>
///
/// <para><b>It can only slow a job down, never speed one up</b> — the cron remains the
/// ceiling on how often the job is offered a chance to run. That asymmetry is deliberate and
/// is stated on the page, because a control that silently fails to do half of what it looks
/// like it does is worse than no control.</para>
///
/// <para>FLEET-WIDE, one row per <see cref="FunctionName"/> — not edition-scoped. Several jobs
/// (the ERP reconcile, the audit purge) run for the whole fleet rather than one edition, so a
/// per-edition throttle would be ambiguous for exactly the jobs most worth throttling. Same
/// reasoning as <see cref="JobHealthMarker"/>.</para>
/// </summary>
public class JobRunState
{
    public int Id { get; set; }

    /// <summary>
    /// The Azure Functions name, exactly as in <c>[Function("…")]</c> — the join key to
    /// <c>JobCatalog</c>, the middleware and the logs. Unique.
    /// </summary>
    public string FunctionName { get; set; } = string.Empty;

    /// <summary>
    /// Minimum minutes between two runs. <b>0 = no limit</b> (the cron alone decides), which
    /// is the default for every job so behaviour is unchanged until an operator intervenes.
    /// A value BELOW the job's natural cron interval has no effect — it cannot manufacture
    /// extra runs.
    /// </summary>
    public int MinIntervalMinutes { get; set; }

    /// <summary>
    /// When the job was last allowed to START (UTC). Stamped BEFORE the body runs, on purpose:
    /// a job that crashes still counts as having run, so a throttle can never become a retry
    /// loop that hammers a failing dependency.
    /// </summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>When an invocation was last SKIPPED by the throttle — so the page can show
    /// that a limit is actually biting, rather than leaving the operator to infer it.</summary>
    public DateTimeOffset? LastThrottledAt { get; set; }

    /// <summary>How many invocations the throttle has skipped in total (diagnostic).</summary>
    public int ThrottledCount { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Who last changed the interval — an operational change worth attributing.</summary>
    public string? UpdatedByEmail { get; set; }
}
