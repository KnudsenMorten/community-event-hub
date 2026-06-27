using CommunityHub.Core.Data;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// Org-admin master switch (operator 2026-06-23): when an organizer PAUSES all
/// background jobs from Organizer → Settings, every timer job in this worker
/// no-ops BEFORE doing any work. One central guard so individual jobs don't each
/// need their own check.
///
/// The pause is per ACTIVE edition (<see cref="FeatureGateService.AreJobsPausedAsync"/>);
/// a missing flag means NOT paused, so default behaviour is unchanged. The flag is
/// re-read every invocation, so RESUME takes effect on each job's next tick.
///
/// <see cref="EnableEmailFeaturesJob"/> is exempt so the operator can still bootstrap
/// per-edition feature state while everything else is paused.
/// </summary>
public sealed class JobsPauseMiddleware : IFunctionsWorkerMiddleware
{
    // Admin/bootstrap functions that must still run while paused.
    private static readonly HashSet<string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(EnableEmailFeaturesJob),
        // HTTP webhook (§128): the pause middleware short-circuits with no HTTP response,
        // which would surface as a host 500 to Zoho. The webhook handler enforces the pause
        // itself and returns a clean 200 no-op, so it must bypass this middleware.
        nameof(ZohoOrderWebhook),
    };

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var fn = context.FunctionDefinition.Name;
        if (Exempt.Contains(fn))
        {
            await next(context);
            return;
        }

        var ct = context.CancellationToken;
        var db = context.InstanceServices.GetRequiredService<CommunityHubDbContext>();
        var gate = context.InstanceServices.GetRequiredService<FeatureGateService>();

        var eventId = await db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);

        if (eventId is not null && await gate.AreJobsPausedAsync(eventId.Value, ct))
        {
            context.InstanceServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("JobsPauseMiddleware")
                .LogInformation(
                    "Background jobs are PAUSED for the edition (org-admin master switch); "
                    + "skipping {Function}.", fn);
            return; // short-circuit: the function body never runs
        }

        await next(context);
    }
}
