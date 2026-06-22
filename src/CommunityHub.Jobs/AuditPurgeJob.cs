using CommunityHub.Core.Audit;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// RETENTION (REQUIREMENTS §24): deletes audit-trail entries older than the retention
/// window (operator 2026-06-22: 6 months) so the <c>AuditEntries</c> table doesn't grow
/// unbounded. Pure data hygiene — no feature gate, never sends anything.
/// </summary>
public sealed class AuditPurgeJob
{
    /// <summary>Retention window — entries older than this are purged (operator: 6 months).</summary>
    public const int RetentionMonths = 6;

    private readonly IAuditTrail _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditPurgeJob> _log;

    public AuditPurgeJob(IAuditTrail audit, TimeProvider clock, ILogger<AuditPurgeJob> log)
    {
        _audit = audit;
        _clock = clock;
        _log = log;
    }

    /// <summary>Daily at 04:00 UTC (after the 03:30 welcome-grant prune).</summary>
    [Function("AuditPurgeJob")]
    public async Task Run([TimerTrigger("0 0 4 * * *")] TimerInfo timer, CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow().AddMonths(-RetentionMonths);
        var removed = await _audit.PurgeOlderThanAsync(cutoff, ct);
        _log.LogInformation(
            "AuditPurgeJob: purged {Count} audit entr(y/ies) older than {Cutoff:u} ({Months}-month retention).",
            removed, cutoff, RetentionMonths);
    }
}
