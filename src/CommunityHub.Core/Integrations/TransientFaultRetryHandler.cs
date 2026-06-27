using System.Net;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// A small, bounded, jittered transient-fault retry for an <see cref="HttpClient"/>
/// pipeline (operator 2026-06-27, the ErpWebshopReconcile incident: a momentary
/// upstream <c>503</c> from Company Manager's <c>GetCompanyUsers</c> crashed the whole
/// reconcile). It retries only on TRANSIENT conditions — HTTP 5xx, 408 (RequestTimeout),
/// 429 (TooManyRequests), and transient transport exceptions (network failures + an
/// <see cref="HttpClient"/> per-request TIMEOUT, NOT a caller cancellation) — with an
/// exponential, jittered back-off, capped at <see cref="_maxRetries"/> attempts.
///
/// Why a hand-rolled handler rather than Microsoft.Extensions.Http.Resilience /
/// Polly: neither package is referenced anywhere in this solution, and the operator
/// asked to keep a new dependency optional. This handler is ~one screen, has no extra
/// package surface, and is registered the same way (one <c>AddHttpMessageHandler</c>
/// line per client). It is deliberately conservative (retry only, no circuit-breaker
/// or rate-limiter) so behaviour is obvious.
///
/// Re-sending the same <see cref="HttpRequestMessage"/> is supported on modern .NET
/// (Core 3.0+); the Company Manager client only issues body-less GETs and re-readable
/// <c>StringContent</c> writes, so a retry never fails on a consumed request body.
/// </summary>
public sealed class TransientFaultRetryHandler : DelegatingHandler
{
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeProvider _clock;
    // Test seam: when set, replaces the computed back-off so unit tests don't sleep.
    private readonly Func<int, TimeSpan>? _delayOverride;

    /// <param name="maxRetries">Max RETRIES after the first attempt (default 3 ⇒ up to 4 sends).</param>
    /// <param name="baseDelay">Base back-off (default 500 ms; doubles per attempt + jitter).</param>
    public TransientFaultRetryHandler(
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        TimeProvider? clock = null,
        Func<int, TimeSpan>? delayOverride = null)
    {
        _maxRetries = Math.Max(0, maxRetries);
        _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(500);
        _clock = clock ?? TimeProvider.System;
        _delayOverride = delayOverride;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, ct);
                if (attempt >= _maxRetries || !IsTransient(response.StatusCode))
                {
                    return response;
                }
                // Transient status + retries left: dispose this response and back off.
                response.Dispose();
            }
            catch (Exception ex) when (attempt < _maxRetries && IsTransientException(ex, ct))
            {
                response?.Dispose();
                // fall through to the back-off + retry
            }

            await DelayAsync(attempt, ct);
        }
    }

    private async Task DelayAsync(int attempt, CancellationToken ct)
    {
        var delay = _delayOverride?.Invoke(attempt) ?? ComputeDelay(attempt);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _clock, ct);
        }
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        // Exponential: base, 2x, 4x … plus up to half-a-base of random jitter so a
        // fleet of callers does not retry in lock-step (thundering herd).
        var expoMs = _baseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var jitterMs = Random.Shared.NextDouble() * (_baseDelay.TotalMilliseconds / 2);
        return TimeSpan.FromMilliseconds(expoMs + jitterMs);
    }

    private static bool IsTransient(HttpStatusCode code) =>
        (int)code >= 500
        || code == HttpStatusCode.RequestTimeout      // 408
        || code == HttpStatusCode.TooManyRequests;    // 429

    private static bool IsTransientException(Exception ex, CancellationToken ct)
    {
        // A real caller cancellation (our ct) is NOT transient — never retry it.
        if (ct.IsCancellationRequested) return false;
        return ex switch
        {
            HttpRequestException => true,
            // An HttpClient per-request TIMEOUT surfaces as TaskCanceledException
            // (an OperationCanceledException) with ct NOT requested — that is transient.
            TaskCanceledException => true,
            OperationCanceledException => true,
            TimeoutException => true,
            _ => false,
        };
    }
}
