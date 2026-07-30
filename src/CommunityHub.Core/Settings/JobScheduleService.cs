using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Settings;

/// <summary>One row on the organizer jobs page: the catalog description joined to live state.</summary>
public sealed record JobStatusRow(
    JobDescriptor Job,
    int MinIntervalMinutes,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? LastThrottledAt,
    int ThrottledCount,
    bool FeatureEnabled,
    DateTimeOffset? LastSuccessAt,
    int ConsecutiveFailures,
    // §545(a)/§627 — how many runs in a row this job has done NOTHING, and the reason it gave.
    // 0 / null for a job that has not been instrumented (silence = UNKNOWN, never a claim).
    int ConsecutiveNoOps = 0,
    string? LastNoOpReason = null)
{
    /// <summary>
    /// §510 — the cadence actually in force: what the operator set, else the catalog default.
    /// 0 means the cron alone decides (a clock-anchored job with no override).
    /// </summary>
    public int EffectiveIntervalMinutes =>
        JobThrottle.EffectiveIntervalMinutes(Job, MinIntervalMinutes);

    /// <summary>
    /// True when an operator has limited this job below its natural cadence — which earns the
    /// "LIMITED" badge.
    ///
    /// <para>§510: NEVER true for an interval-driven job. There, a value is simply the job's
    /// frequency, not a restraint on it, so badging it as limited would put a warning on every
    /// converted job and mean the opposite of what it says.</para>
    /// </summary>
    public bool IsThrottled => !Job.IsIntervalDriven && MinIntervalMinutes > 0;

    /// <summary>True when the job cannot do anything because its feature switch is off —
    /// the most common reason a job "isn't running" and the hardest to guess.</summary>
    public bool BlockedByFeature => Job.FeatureKey is not null && !FeatureEnabled;

    /// <summary>Consecutive failures are the health signal the alerting already uses.</summary>
    public bool IsUnhealthy => ConsecutiveFailures > 0;

    /// <summary>
    /// §545(a) — the job is GREEN AND DOING NOTHING, for long enough that saying so is useful.
    /// </summary>
    /// <remarks>
    /// <para>This is the state the whole page was missing. §544: the session push was switched off
    /// by a setting, said so every run for weeks at Information level, and this page showed *"a
    /// healthy job with a recent last run"* — until 1,500 attendees had no agenda. The row looked
    /// identical to a working one.</para>
    ///
    /// <para>🔒 The bar is 12 runs, not 1. A single doing-nothing run is ordinary (nothing had
    /// changed); a dozen in a row with the same reason is a pattern. Lower than the 100-run ALERT
    /// threshold on purpose: a badge he only sees when he visits the page can afford to speak up
    /// far earlier than a mail that arrives uninvited (§609).</para>
    /// </remarks>
    public bool IsInactive => ConsecutiveNoOps >= InactiveBadgeThreshold;

    /// <summary>Consecutive no-op runs before the page badges a job as inactive.</summary>
    public const int InactiveBadgeThreshold = 12;
}

/// <summary>
/// §327 — reads and writes the operator-editable job throttle, and joins it to
/// <see cref="JobCatalog"/> + feature state + health for the organizer jobs page.
///
/// <para>The THROTTLE is enforced centrally in <c>JobsPauseMiddleware</c>, not here: this
/// service is the read/write surface, the middleware is the single chokepoint every job
/// already passes through. Putting the check anywhere else would mean editing 21 jobs and
/// hoping the 22nd remembers.</para>
/// </summary>
public sealed class JobScheduleService
{
    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly TimeProvider _clock;

    public JobScheduleService(CommunityHubDbContext db, FeatureGateService gate, TimeProvider clock)
    {
        _db = db;
        _gate = gate;
        _clock = clock;
    }

    /// <summary>
    /// Every job in the catalog with its live state, in catalog order (most frequent first).
    /// A job with no state row yet simply reads as "no limit, never run" — the page must list
    /// all 21 whether or not the middleware has touched them.
    /// </summary>
    public async Task<IReadOnlyList<JobStatusRow>> BuildAsync(int eventId, CancellationToken ct = default)
    {
        var states = await _db.JobRunStates.AsNoTracking()
            .ToDictionaryAsync(s => s.FunctionName, StringComparer.Ordinal, ct);

        var health = await _db.JobHealthMarkers.AsNoTracking()
            .ToDictionaryAsync(h => h.JobKey, StringComparer.Ordinal, ct);

        var rows = new List<JobStatusRow>();
        foreach (var job in JobCatalog.All)
        {
            states.TryGetValue(job.FunctionName, out var st);

            var featureEnabled = job.FeatureKey is null
                || await _gate.IsFeatureEnabledAsync(job.FeatureKey, eventId, ct);

            JobHealthMarker? hm = null;
            if (job.HealthKey is not null) health.TryGetValue(job.HealthKey, out hm);

            rows.Add(new JobStatusRow(
                job,
                st?.MinIntervalMinutes ?? 0,
                st?.LastRunAt,
                st?.LastThrottledAt,
                st?.ThrottledCount ?? 0,
                featureEnabled,
                hm?.LastSuccessAt,
                hm?.ConsecutiveFailures ?? 0,
                hm?.ConsecutiveNoOps ?? 0,
                hm?.LastNoOpReason));
        }
        return rows;
    }

    /// <summary>
    /// Set (or clear, with 0) a job's minimum interval. Refuses a function name that is not in
    /// the catalog — a typo must not create a throttle row that silently governs nothing, or
    /// worse, one that governs a job nobody can see on the page.
    /// </summary>
    public async Task<bool> SetMinIntervalAsync(
        string functionName, int minutes, string? actorEmail, CancellationToken ct = default)
    {
        if (JobCatalog.Find(functionName) is null) return false;

        // Clamp: negative is a typo, and a week is far past any sane cadence. 0 = no limit.
        minutes = Math.Clamp(minutes, 0, 10080);

        var row = await _db.JobRunStates
            .FirstOrDefaultAsync(s => s.FunctionName == functionName, ct);

        if (row is null)
        {
            row = new JobRunState { FunctionName = functionName };
            _db.JobRunStates.Add(row);
        }

        row.MinIntervalMinutes = minutes;
        row.UpdatedAt = _clock.GetUtcNow();
        row.UpdatedByEmail = actorEmail;

        await _db.SaveChangesAsync(ct);
        return true;
    }
}
