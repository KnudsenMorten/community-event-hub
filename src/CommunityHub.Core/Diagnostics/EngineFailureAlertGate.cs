using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Diagnostics;

/// <summary>
/// Central "alert only on the 2nd+ CONSECUTIVE failure" decision for EVERY background
/// function, so a single transient blip (e.g. a WooCommerce <c>503 Service Unavailable</c>
/// or a momentary platform glitch) does NOT email anyone — only a real, repeated failure
/// pages the developer (operator agreement, 2026-06-27 ErpWebshopReconcile 503 incident,
/// REQUIREMENTS §138).
///
/// This is the small, fully-testable orchestration that <see cref="System.Object"/>-free
/// callers (the <c>EngineErrorAlertMiddleware</c> in the Jobs worker) delegate to. It wraps
/// the durable <see cref="JobFailureTracker"/> (one <see cref="Domain.JobHealthMarker"/> row
/// per function, so the consecutive-failure count SURVIVES a Flex-Consumption scale-to-zero /
/// recycle between hourly ticks — an in-memory counter would reset and never reach 2) plus the
/// ring-exempt <see cref="EngineAlertSender"/> (whose 6h per-key throttle stays as a SECONDARY
/// anti-flood guard). The full sequence is therefore:
/// <c>2 consecutive failures → alert → at most once per 6h per function</c>.
/// </summary>
public sealed class EngineFailureAlertGate
{
    /// <summary>
    /// Consecutive failures of the SAME function required before an alert is sent. Operator
    /// agreement (2026-06-27): a single failure is likely transient, so suppress it and only
    /// page on the 2nd in a row. Named const so the gate is tunable in one place.
    /// </summary>
    public const int ConsecutiveFailureAlertThreshold = JobFailureTracker.DefaultAlertThreshold; // = 2

    private readonly JobFailureTracker _failures;
    private readonly EngineAlertSender _alerts;
    private readonly ILogger<EngineFailureAlertGate> _log;

    public EngineFailureAlertGate(
        JobFailureTracker failures, EngineAlertSender alerts, ILogger<EngineFailureAlertGate> log)
    {
        _failures = failures;
        _alerts = alerts;
        _log = log;
    }

    /// <summary>
    /// A function completed WITHOUT throwing: reset its consecutive-failure counter to 0 and
    /// stamp the last-success time. Cheap + FAIL-SAFE — a state-store error here must never fail
    /// an otherwise successful job, so it is swallowed and logged.
    /// </summary>
    public async Task OnSuccessAsync(string functionName, CancellationToken ct = default)
    {
        try
        {
            await _failures.RecordSuccessAsync(functionName, ct);
        }
        catch (Exception ex)
        {
            // A bookkeeping failure must NOT turn a good run into a failed one.
            _log.LogWarning(ex,
                "EngineFailureAlertGate[{Fn}]: could not record success (state store unavailable); ignored.",
                functionName);
        }
    }

    /// <summary>
    /// §545(b) INACTIVE — how many consecutive runs a job may do NOTHING before the operator is
    /// told. Deliberately high.
    /// </summary>
    /// <remarks>
    /// 🔒 A doing-nothing run is usually CORRECT — a feature is off because he switched it off, and
    /// he does not need telling every 10 minutes. The alert exists for the case where a job has been
    /// inert so long that the reason has been FORGOTTEN, which is exactly §544 (weeks). At the
    /// 10-minute cadence of the sync jobs, 100 runs is about 17 hours — long enough that no
    /// deliberate same-day toggle trips it, short enough that "weeks" is impossible.
    /// </remarks>
    public const int ConsecutiveNoOpAlertThreshold = 100;

    /// <summary>
    /// §545(b) — record what a clean run ACHIEVED, and alert once a job has been doing nothing for
    /// so long that the reason has plainly been forgotten. Never throws.
    /// </summary>
    public async Task OnActivityAsync(
        string functionName, string? inactiveReason, CancellationToken ct = default)
    {
        int streak;
        try
        {
            streak = await _failures.RecordActivityAsync(functionName, inactiveReason, ct);
        }
        catch (Exception ex)
        {
            // 🔒 FAIL QUIET here, the opposite of OnFailureAsync. That one fails OPEN because a
            // missed FAILURE is an outage; this one is a nice-to-know, and alerting on a bookkeeping
            // error would be the §609 mistake — a red mail for a condition that is not a problem.
            _log.LogWarning(ex,
                "EngineFailureAlertGate[{Fn}]: could not record run activity; ignored.", functionName);
            return;
        }

        if (streak == 0 || streak % ConsecutiveNoOpAlertThreshold != 0) return;

        var fnEnc = System.Net.WebUtility.HtmlEncode(functionName);
        var reasonEnc = System.Net.WebUtility.HtmlEncode(inactiveReason ?? "(no reason given)");

        await _alerts.AlertAsync(
            $"Engine INACTIVE: {functionName} [ELDK27]",
            $"<p>The background engine <b>{fnEnc}</b> has now run <b>{streak}</b> times in a row "
            + "WITHOUT DOING ANYTHING. It is not failing — it is being turned away every time, "
            + "always for the same reason:</p>"
            + $"<blockquote><b>{reasonEnc}</b></blockquote>"
            + "<p>If that is deliberate, nothing needs doing and this will not be repeated until it "
            + "has run another " + ConsecutiveNoOpAlertThreshold + " times. If it is not, this job "
            + "has been silently switched off and whatever it feeds is not being updated.</p>",
            ct, throttleKey: $"engine-inactive:{functionName}");

        _log.LogWarning(
            "EngineFailureAlertGate[{Fn}]: INACTIVE for {N} consecutive runs — {Reason}.",
            functionName, streak, inactiveReason);
    }

    /// <summary>
    /// A function THREW: increment its durable consecutive-failure counter and send the ops
    /// alert ONLY once the count reaches <see cref="ConsecutiveFailureAlertThreshold"/>. The
    /// caller is still responsible for re-throwing the original exception (platform
    /// retry/recording) — this method NEVER throws and NEVER replaces the real failure.
    ///
    /// FAIL OPEN on a state-store error: if the counter cannot be read/updated we do NOT
    /// silently swallow what might be a genuine outage — we alert anyway, so a real failure is
    /// never lost to a database problem. (The <see cref="EngineAlertSender"/> 6h throttle still
    /// guards against a flood.)
    /// </summary>
    public async Task OnFailureAsync(string functionName, Exception failure, CancellationToken ct = default)
    {
        bool shouldAlert;
        int consecutive;
        try
        {
            var decision = await _failures.RecordFailureAsync(
                functionName, failure.Message, ct, ConsecutiveFailureAlertThreshold);
            shouldAlert = decision.ShouldAlert;
            consecutive = decision.ConsecutiveFailures;
        }
        catch (Exception stateEx)
        {
            // Durable state unavailable — FAIL OPEN (alert) rather than risk silently dropping a
            // real outage. Unknown count is signalled with -1.
            _log.LogWarning(stateEx,
                "EngineFailureAlertGate[{Fn}]: failure-state store unavailable; failing OPEN (alerting).",
                functionName);
            shouldAlert = true;
            consecutive = -1;
        }

        if (!shouldAlert)
        {
            _log.LogInformation(
                "EngineFailureAlertGate[{Fn}]: consecutive failure #{N} below threshold {T}; "
                + "alert suppressed (likely transient).",
                functionName, consecutive, ConsecutiveFailureAlertThreshold);
            return;
        }

        // §701.1 — a store-unavailable fail-open is NOT a per-job event. The state store is the
        // database, so when it is unreachable EVERY job fails open in the same minute and, under a
        // per-function throttle key, each one sent its own mail. That is the operator's "3 erors
        // per mail" (2026-07-29): a single ~5-minute Azure SQL blip produced one alert per engine,
        // all with the same root cause, and the §138 consecutive-failure gate was bypassed for all
        // of them precisely because the counter it reads was the thing that was down.
        //
        // ⇒ Coalesce onto ONE shared throttle key so the 6h window collapses the storm to a single
        // mail. The FIRST engine to notice names itself in the subject; the body says plainly that
        // the others are suppressed, so a coalesced alert can never read as "only this one broke".
        var storeUnavailable = consecutive < 0;

        var countText = storeUnavailable
            ? "FAILED and the durable failure-state store could not be read (failing OPEN)"
            : $"now FAILED <b>{consecutive}</b> time(s) in a row, so this is no longer a one-off "
              + "platform/upstream glitch";

        var fnEnc = System.Net.WebUtility.HtmlEncode(functionName);
        var html =
            $"<p>The background engine <b>{fnEnc}</b> has {countText}.</p>"
            + $"<pre>{System.Net.WebUtility.HtmlEncode(failure.ToString())}</pre>";

        if (storeUnavailable)
        {
            html +=
                "<p><b>This alert is coalesced.</b> The failure-state store is the database, so if it "
                + "is unreachable then every other engine is failing the same way at the same time — "
                + "they are suppressed for 6 hours rather than sending you one mail each. "
                + "<b>Read this as \"the database was unreachable\", not as \"this one engine broke\".</b></p>"
                + "<p>The engines recover on their own once the database is reachable; a short blip "
                + "needs no action. Check whether they are running again before investigating.</p>";
        }

        // Subject unchanged (operator contract). Per-function throttle key normally, so one stuck job
        // can't flood; ONE SHARED key when the store is down, so N broken jobs can't flood either.
        var throttleKey = storeUnavailable
            ? "engine-fail:state-store-unavailable"
            : $"engine-fail:{functionName}";

        await _alerts.AlertAsync(
            $"Engine FAILED: {functionName} [ELDK27]", html, ct, throttleKey: throttleKey);
    }
}
