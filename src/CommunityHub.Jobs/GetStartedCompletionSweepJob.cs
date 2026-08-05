using CommunityHub.Core.Data;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §746 — every 5 minutes, report anyone who has just finished Get Started.
/// </summary>
/// <remarks>
/// <para>§720 hooked the notice into <c>/Forms/Wizard</c>, but people finish on the standalone form
/// pages too (<c>/Forms/Hotel</c>, <c>/Forms/Lunch</c>, …), so no mail was ever sent. Those pages
/// share no base class; wiring each would break again the next time one is added. This observes
/// completion instead of depending on where it happened.</para>
///
/// <para>🔒 <b>Cheap by construction</b> (operator: *"but dont impact performance necessary with this
/// against sql"*). The 5-minute pass first asks the AUDIT TRAIL who has done anything in the last
/// 15 minutes — an index seek on <c>(EventId, OccurredUtc)</c> — and stops there when the answer is
/// nobody, which is almost always. Only those few participants get a wizard rebuild.</para>
///
/// <para>🔑 <b>Once an hour it runs a FULL sweep</b> (at minute 0). That is the backstop for a
/// completion nobody clicked for themselves — an organiser edit, a reconciler, a job — where the
/// audit actor is someone else and the fast path cannot see it. Rare enough to be free, important
/// enough not to skip.</para>
///
/// <para>Sending is idempotent (once ever, ledger-keyed), so overlapping passes cannot double-mail.
/// Gated on the same <c>getstarted-complete-notice</c> switch as the wizard hook — one control, both
/// paths, exactly as he asked in §720.</para>
/// </remarks>
public sealed class GetStartedCompletionSweepJob
{
    private readonly CommunityHubDbContext _db;
    private readonly GetStartedCompletionSweep _sweep;
    private readonly FeatureGateService _gate;
    private readonly ILogger<GetStartedCompletionSweepJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    /// <summary>§878 — when the hourly FULL sweep last ran, so it no longer depends on the tick
    /// minute (which a settable interval drifts away from). Static: Functions may construct a new
    /// instance per invocation, so per-instance state would make every pass a full sweep.</summary>
    private static DateTime? _lastFullSweepUtc;

    public GetStartedCompletionSweepJob(
        CommunityHubDbContext db, GetStartedCompletionSweep sweep, FeatureGateService gate,
        ILogger<GetStartedCompletionSweepJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db;
        _sweep = sweep;
        _gate = gate;
        _log = log;
        _activity = activity;
    }

    [Function("GetStartedCompletionSweepJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _activity?.ReportInactive("No active edition, so there is nobody to check.");
            return;
        }

        if (!await _gate.IsFeatureEnabledAsync(
                GetStartedCompletionNotifier.FeatureKey, eventId.Value, ct))
        {
            _activity?.ReportInactive(
                "The 'Notify me when someone completes Get Started' switch is off, so no completion "
                + "notices are sent.");
            return;
        }

        // 🔒 §878 — WAS `DateTime.UtcNow.Minute < 5`, and that had to go before this job could be
        // made operator-settable. A stored interval is spaced from the LAST RUN, not aligned to the
        // wall clock, so it drifts: the tick would slowly stop landing in minutes 0–4 and the full
        // sweep would then NEVER run again — silently, with the job still reporting green. That is
        // §544's exact failure shape, and it is invisible precisely because nothing fails.
        //
        // Elapsed time is the honest test, and it holds at ANY interval he sets: the deep sweep
        // runs when an hour has passed since the last one, whether he polls every 5 minutes or
        // every 6 hours. `_lastFullSweepUtc` is per-instance, so a host restart simply causes one
        // extra full sweep — the sweep is idempotent, so that costs a query and nothing else.
        var nowUtc = DateTime.UtcNow;
        var fullSweep = _lastFullSweepUtc is null
                        || nowUtc - _lastFullSweepUtc.Value >= TimeSpan.FromHours(1);
        if (fullSweep) _lastFullSweepUtc = nowUtc;

        var sent = await _sweep.RunAsync(eventId.Value, fullSweep, ct);

        _activity?.ReportWork();
        if (sent > 0)
        {
            _log.LogInformation(
                "GetStartedCompletionSweepJob: {Sent} completion notice(s) sent (fullSweep={Full}).",
                sent, fullSweep);
        }
    }
}
