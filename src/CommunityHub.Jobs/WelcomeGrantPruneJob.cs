using CommunityHub.Core.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// Housekeeping: prunes welcome auto-login grants (<c>MagicLinkGrant</c>) that can
/// no longer be redeemed (consumed, revoked, or expired) AND are older than the
/// audit-retention window, across all editions. Active grants are never touched,
/// and recent dead grants are kept so the "was this link used?" audit survives.
/// REQUIREMENTS §4 "optional periodic prune of expired/consumed rows".
///
/// No feature gate — this is pure data hygiene, never sends anything.
/// </summary>
public sealed class WelcomeGrantPruneJob
{
    private readonly WelcomeGrantAdminService _admin;
    private readonly TimeProvider _clock;
    private readonly ILogger<WelcomeGrantPruneJob> _log;

    public WelcomeGrantPruneJob(
        WelcomeGrantAdminService admin,
        TimeProvider clock,
        ILogger<WelcomeGrantPruneJob> log)
    {
        _admin = admin;
        _clock = clock;
        _log = log;
    }

    /// <summary>Daily at 03:30 UTC.</summary>
    [Function("WelcomeGrantPruneJob")]
    public async Task Run(
        [TimerTrigger("0 30 3 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var removed = await _admin.PruneAsync(_clock.GetUtcNow(), ct: ct);
        _log.LogInformation(
            "WelcomeGrantPruneJob: pruned {Count} dead welcome grant(s) older than the retention window.",
            removed);
    }
}
