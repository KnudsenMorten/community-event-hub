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

        var countText = consecutive >= 0
            ? $"now FAILED <b>{consecutive}</b> time(s) in a row, so this is no longer a one-off "
              + "platform/upstream glitch"
            : "FAILED and the durable failure-state store could not be read (failing OPEN)";

        var fnEnc = System.Net.WebUtility.HtmlEncode(functionName);
        var html =
            $"<p>The background engine <b>{fnEnc}</b> has {countText}.</p>"
            + $"<pre>{System.Net.WebUtility.HtmlEncode(failure.ToString())}</pre>";

        // Subject unchanged (operator contract). Stable per-function throttle key so a job stuck
        // failing every tick can't flood the inbox (EngineAlertSender suppresses within 6h).
        await _alerts.AlertAsync(
            $"Engine FAILED: {functionName} [ELDK27]", html, ct, throttleKey: $"engine-fail:{functionName}");
    }
}
