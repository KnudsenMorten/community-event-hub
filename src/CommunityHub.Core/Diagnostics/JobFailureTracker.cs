using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Diagnostics;

/// <summary>
/// Persisted "alert only on N consecutive failures" gate for background jobs
/// (operator 2026-06-27, the ErpWebshopReconcile 503 incident: "only ALERT if it
/// fails 2 times in a row — a single failure is likely a backup/platform glitch").
///
/// A job calls <see cref="RecordSuccessAsync"/> on a clean run (resets the counter)
/// and <see cref="RecordFailureAsync"/> when it throws (increments the counter +
/// returns whether the alert threshold has been reached). State lives in a single
/// fleet-wide <see cref="JobHealthMarker"/> row per job key, so the decision SURVIVES
/// process restarts between the 30-min ticks — two genuinely consecutive failures
/// alert even across a redeploy. The job is responsible for ALWAYS recording the
/// failure (audit/log) for observability; this tracker only decides whether to PAGE.
/// </summary>
public sealed class JobFailureTracker
{
    /// <summary>Consecutive failures required before an alert is raised (operator: 2).</summary>
    public const int DefaultAlertThreshold = 2;

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<JobFailureTracker> _log;

    public JobFailureTracker(CommunityHubDbContext db, TimeProvider clock, ILogger<JobFailureTracker> log)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    /// <summary>The outcome of recording a failure: the new consecutive count and whether to alert.</summary>
    public readonly record struct FailureDecision(int ConsecutiveFailures, bool ShouldAlert);

    /// <summary>
    /// Record that <paramref name="jobKey"/> just FAILED. Increments the persisted
    /// consecutive-failure counter and returns it together with
    /// <see cref="FailureDecision.ShouldAlert"/> = (count &gt;= <paramref name="alertThreshold"/>).
    /// So the 1st failure returns ShouldAlert=false (suppressed — likely transient) and
    /// the 2nd consecutive returns ShouldAlert=true.
    /// </summary>
    public async Task<FailureDecision> RecordFailureAsync(
        string jobKey, string? error, CancellationToken ct = default, int alertThreshold = DefaultAlertThreshold)
    {
        var now = _clock.GetUtcNow();
        var marker = await GetOrCreateAsync(jobKey, ct);
        marker.ConsecutiveFailures += 1;
        marker.LastFailureAt = now;
        marker.LastError = Truncate(error, 1000);
        marker.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var shouldAlert = marker.ConsecutiveFailures >= alertThreshold;
        _log.LogInformation(
            "JobFailureTracker[{Key}]: consecutive failure #{N} (alert threshold {T}) -> alert {Alert}.",
            jobKey, marker.ConsecutiveFailures, alertThreshold, shouldAlert ? "RAISED" : "suppressed");
        return new FailureDecision(marker.ConsecutiveFailures, shouldAlert);
    }

    /// <summary>
    /// Record that <paramref name="jobKey"/> just SUCCEEDED — resets the consecutive
    /// counter to 0 and stamps <see cref="JobHealthMarker.LastSuccessAt"/>. Idempotent.
    /// </summary>
    public async Task RecordSuccessAsync(string jobKey, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var marker = await GetOrCreateAsync(jobKey, ct);
        marker.ConsecutiveFailures = 0;
        marker.LastSuccessAt = now;
        marker.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// §545(b) INACTIVE — record what a clean run actually ACHIEVED, which
    /// <see cref="RecordSuccessAsync"/> cannot express.
    /// </summary>
    /// <param name="jobKey">The function name.</param>
    /// <param name="inactiveReason">
    /// Why the run did nothing, or null when it did real work. 🔒 Pass null for "did work" — this
    /// method must never be called for a job that reported NOTHING, because silence is UNKNOWN and
    /// must not reset a streak the job is genuinely still in.
    /// </param>
    /// <returns>The consecutive no-op count after this run (0 when it did work).</returns>
    public async Task<int> RecordActivityAsync(
        string jobKey, string? inactiveReason, CancellationToken ct = default)
    {
        var marker = await GetOrCreateAsync(jobKey, ct);

        if (string.IsNullOrWhiteSpace(inactiveReason))
        {
            // Did work. Whatever it was stuck on, it isn't now.
            marker.ConsecutiveNoOps = 0;
            marker.LastNoOpReason = null;
        }
        else
        {
            // A DIFFERENT reason restarts the count: "feature off" for 40 runs and then "no active
            // edition" are two separate stories, and merging them would report the wrong one.
            if (!string.Equals(marker.LastNoOpReason, inactiveReason, StringComparison.Ordinal))
                marker.ConsecutiveNoOps = 0;

            marker.ConsecutiveNoOps += 1;
            marker.LastNoOpReason = Truncate(inactiveReason, 400);
        }

        marker.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return marker.ConsecutiveNoOps;
    }

    private async Task<JobHealthMarker> GetOrCreateAsync(string jobKey, CancellationToken ct)
    {
        var marker = await _db.JobHealthMarkers.FirstOrDefaultAsync(m => m.JobKey == jobKey, ct);
        if (marker is null)
        {
            marker = new JobHealthMarker { JobKey = jobKey };
            _db.JobHealthMarkers.Add(marker);
        }
        return marker;
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
