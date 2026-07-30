using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>
/// §219 (Risk-4) — paces a BULK email loop. A bulk sender (the reminder engine batch,
/// the attendee welcome provisioning loops) calls <see cref="PaceAsync"/> BETWEEN
/// consecutive sends so a ~1500-recipient run does not trip Brevo's per-second rate
/// limit. A SINGLE interactive send (PIN sign-in, one re-send) does NOT use the pacer,
/// so it stays fast — pacing is applied only where many messages go out back-to-back.
///
/// <para>Injected as a seam so a unit test can assert the pace is applied (count the
/// calls) WITHOUT actually sleeping — the test supplies a no-op recording double.</para>
/// </summary>
public interface IBulkSendPacer
{
    /// <summary>The configured inter-send delay in milliseconds (0 = pacing disabled).</summary>
    int DelayMs { get; }

    /// <summary>
    /// Await the inter-send delay. A no-op (returns immediately) when <see cref="DelayMs"/>
    /// is 0 so disabling pacing costs nothing. Honours cancellation.
    /// </summary>
    Task PaceAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IBulkSendPacer"/>: delays <c>Email:BulkSendDelayMs</c> milliseconds
/// (default 150) via <see cref="TimeProvider"/> so a bulk loop is spaced under Brevo's
/// rate limit. Config-driven — the delay is tunable without a code change.
/// </summary>
public sealed class BulkSendPacer : IBulkSendPacer
{
    private readonly int _delayMs;
    private readonly TimeProvider _clock;

    public BulkSendPacer(IOptions<EmailOptions> options, TimeProvider? clock = null)
    {
        _delayMs = Math.Max(0, options.Value.BulkSendDelayMs);
        _clock = clock ?? TimeProvider.System;
    }

    public int DelayMs => _delayMs;

    public Task PaceAsync(CancellationToken cancellationToken = default) =>
        _delayMs <= 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(_delayMs), _clock, cancellationToken);
}
