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
/// per-edition feature state while everything else is paused.
/// </summary>
public sealed class JobsPauseMiddleware : IFunctionsWorkerMiddleware
{
    // Admin/bootstrap functions that must still run while paused.
    private static readonly HashSet<string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
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

        // §327/§510 CADENCE — the operator's "Runs every N minutes" from /Organizer/Jobs.
        //
        // Enforced HERE because this is the one place every timer job already passes through;
        // the alternative is editing 21 jobs and trusting the 22nd to remember.
        //
        // The cron remains the ceiling on how often an invocation is even OFFERED. On a
        // clock-anchored job (§510 excludes those) this can therefore only slow things down. On an
        // interval-driven job the cron is deliberately a fast base tick, so this value is the real
        // cadence.
        //
        // LastRunAt is stamped BEFORE the body runs, deliberately: a job that crashes still
        // counts as having run, so this can never become a retry loop hammering a failing
        // dependency.
        var state = await db.JobRunStates.FirstOrDefaultAsync(s => s.FunctionName == fn, ct);
        var now = DateTimeOffset.UtcNow;

        // §510 — the operator's value WINS; the catalog default applies only while he has not set
        // one. Both the fallback and the grace inside ShouldSkip are load-bearing — see
        // <see cref="JobThrottle"/>, which holds the rule so it can be tested.
        var descriptor = JobCatalog.Find(fn);
        var effectiveInterval = JobThrottle.EffectiveIntervalMinutes(
            descriptor, state?.MinIntervalMinutes ?? 0);

        // ShouldSkip only returns true when there IS a LastRunAt, which implies a state row.
        if (state is not null && JobThrottle.ShouldSkip(effectiveInterval, state.LastRunAt, now))
        {
            state.LastThrottledAt = now;
            state.ThrottledCount++;
            await db.SaveChangesAsync(ct);

            context.InstanceServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("JobsPauseMiddleware")
                .LogInformation(
                    "{Function} skipped: runs every {Min} min; last run {Last:u}.",
                    fn, effectiveInterval, state.LastRunAt.Value);
            return; // short-circuit: the function body never runs
        }

        // Stamp the run (upserting the row) so the jobs page shows a real "last run" for EVERY
        // job, not only the few that keep a JobHealthMarker.
        if (state is null)
        {
            db.JobRunStates.Add(new CommunityHub.Core.Domain.JobRunState
            {
                FunctionName = fn,
                MinIntervalMinutes = 0,
                LastRunAt = now,
            });
        }
        else
        {
            state.LastRunAt = now;
        }
        await db.SaveChangesAsync(ct);

        await next(context);
    }
}
