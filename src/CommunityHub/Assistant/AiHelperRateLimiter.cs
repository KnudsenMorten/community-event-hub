using System.Collections.Concurrent;

namespace CommunityHub.Assistant;

/// <summary>
/// A tiny in-memory, per-participant rate limiter for the assistant endpoint: at most
/// <see cref="MaxPerWindow"/> questions per rolling <see cref="Window"/>. Singleton;
/// best-effort (process-local — fine for the single-instance hub, and a generous
/// limit anyway). Keeps a short ring of recent hit timestamps per participant and
/// prunes on each check so memory stays bounded.
/// </summary>
public sealed class AiHelperRateLimiter
{
    /// <summary>Max questions allowed per window per participant.</summary>
    public const int MaxPerWindow = 6;

    /// <summary>The rolling window length.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<int, Queue<DateTimeOffset>> _hits = new();

    public AiHelperRateLimiter(TimeProvider clock) => _clock = clock;

    /// <summary>
    /// Record an attempt for <paramref name="participantId"/> and return false when
    /// the participant has exceeded the limit within the current window.
    /// </summary>
    public bool TryAcquire(int participantId)
    {
        var now = _clock.GetUtcNow();
        var cutoff = now - Window;
        var q = _hits.GetOrAdd(participantId, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
            if (q.Count >= MaxPerWindow) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
