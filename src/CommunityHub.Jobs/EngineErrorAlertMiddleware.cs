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
/// Registered OUTERMOST (before <see cref="JobsPauseMiddleware"/>) so it also catches a
/// failure in the pause check itself. Throttled per function name inside
/// <see cref="EngineAlertSender"/> so a job failing every tick can't flood the inbox.
/// </summary>
public sealed class EngineErrorAlertMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var fn = context.FunctionDefinition.Name;
            try
            {
                var alerter = context.InstanceServices.GetService<EngineAlertSender>();
                if (alerter is not null)
                {
                    var html =
                        $"<p>The background engine <b>{System.Net.WebUtility.HtmlEncode(fn)}</b> threw an "
                        + "unhandled exception and did not complete its run.</p>"
                        + $"<pre>{System.Net.WebUtility.HtmlEncode(ex.ToString())}</pre>";
                    await alerter.AlertAsync(
                        $"[ELDK27] Engine FAILED: {fn}", html, context.CancellationToken, throttleKey: $"engine-fail:{fn}");
                }
            }
            catch { /* never let the alert mask the real failure */ }

            throw; // preserve the platform's failure recording + retry semantics
        }
    }
}
