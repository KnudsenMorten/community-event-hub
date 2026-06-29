using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Email;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace CommunityHub.Jobs;

/// <summary>
/// Cross-cutting engine-error alerter (operator 2026-06-25: "as developer I need an
/// email if errors happen in any of the engines — this must be fixed everywhere").
/// Wraps EVERY function invocation; if a job throws an unhandled exception, the
/// developer gets an email (via <see cref="EngineAlertSender"/>, which is ring-exempt
/// so it actually delivers), then the exception is RE-THROWN so the platform's own
/// failure handling/retries are unchanged. One central place ⇒ every timer job is
/// covered, present and future, without touching each job.
///
/// CONSECUTIVE-FAILURE GATE (operator 2026-06-27, REQUIREMENTS §138): the alert is now
/// suppressed on the FIRST failure of a function and sent only on the 2nd+ CONSECUTIVE
/// failure, so a single transient blip (e.g. a WooCommerce 503) does NOT page anyone.
/// The per-function counter is DURABLE (<see cref="EngineFailureAlertGate"/> →
/// <see cref="JobFailureTracker"/> → a <c>JobHealthMarker</c> row), because the Flex
/// Consumption host can scale-to-zero / recycle between hourly ticks and an in-memory
/// counter would reset and never reach 2. A SUCCESS resets the counter. The
/// <see cref="EngineAlertSender"/> 6h per-key throttle stays as a SECONDARY anti-flood.
///
/// All functions are tracked uniformly by function name (a manual/HTTP trigger that fails
/// twice in a row alerting is acceptable). Note: a job that handles its OWN consecutive-
/// failure gate and does NOT re-throw (e.g. <see cref="ErpWebshopReconcileJob"/>) is seen
/// here as a SUCCESS, under its own distinct function-name key — no double counting.
///
/// Registered OUTERMOST (before <see cref="JobsPauseMiddleware"/>) so it also catches a
/// failure in the pause check itself.
/// </summary>
public sealed class EngineErrorAlertMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var fn = context.FunctionDefinition.Name;
        var ct = context.CancellationToken;

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            try
            {
                var gate = context.InstanceServices.GetService<EngineFailureAlertGate>();
                if (gate is not null)
                {
                    // Increment the durable counter; alert only on the 2nd+ consecutive failure.
                    // FAIL OPEN on a state-store error lives inside the gate (alerts anyway).
                    await gate.OnFailureAsync(fn, ex, ct);
                }
                else
                {
                    // DI-wiring fallback (gate unresolvable): fail OPEN to the legacy direct alert
                    // so a real failure is never silently swallowed by a missing registration.
                    var alerter = context.InstanceServices.GetService<EngineAlertSender>();
                    if (alerter is not null)
                    {
                        var html =
                            $"<p>The background engine <b>{System.Net.WebUtility.HtmlEncode(fn)}</b> threw an "
                            + "unhandled exception and did not complete its run.</p>"
                            + $"<pre>{System.Net.WebUtility.HtmlEncode(ex.ToString())}</pre>";
                        await alerter.AlertAsync(
                            $"Engine FAILED: {fn} [ELDK27]", html, ct, throttleKey: $"engine-fail:{fn}");
                    }
                }
            }
            catch { /* never let the alert/state path mask the real failure */ }

            throw; // preserve the platform's failure recording + retry semantics
        }

        // Reached ONLY when the function completed without throwing: reset that function's
        // consecutive-failure counter + stamp last-success. Fail-safe (the gate swallows its
        // own state errors); the extra guard covers a service-resolution hiccup.
        try
        {
            var gate = context.InstanceServices.GetService<EngineFailureAlertGate>();
            if (gate is not null)
                await gate.OnSuccessAsync(fn, ct);
        }
        catch { /* success bookkeeping must never fail a successful job */ }
    }
}
