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
