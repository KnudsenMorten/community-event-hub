namespace CommunityHub.Core.Integrations;

/// <summary>The outcome of one refresh-token exchange.</summary>
/// <param name="Token">The access token, or null when the exchange failed.</param>
/// <param name="ExpiresInSeconds">Zoho's <c>expires_in</c>; 0 when absent (a 1-hour default is assumed).</param>
public readonly record struct ZohoTokenResult(string? Token, int ExpiresInSeconds);

/// <summary>
/// §525 — ONE Zoho access token, shared by every caller until it is nearly expired.
///
/// <para><b>Why this exists.</b> <c>ZohoClient.GetAccessTokenAsync</c> had <b>27 call sites and no
/// caching</b>: every sync pass, every page, every one of ~21 timer jobs exchanged the refresh
/// token for a brand-new access token — despite an access token being valid for about an hour.
/// Zoho <b>rate-limits refresh-token grants</b>, so the app tripped the limit in bursts and every
/// Zoho integration went dark with "No Zoho access token (token refresh failed)", while the very
/// same credential succeeded when tried once by hand. That was the operator's evidence
/// (2026-07-28) and it is exactly the signature of grant throttling, not a dead token.</para>
///
/// <para><b>Registered as a SINGLETON.</b> <c>ZohoClient</c> is created per-resolution through
/// <c>AddHttpClient</c>, so an instance field would cache nothing — the cache has to outlive the
/// client. Optional on the client so tests and legacy constructions keep their old behaviour.</para>
/// </summary>
public sealed class ZohoAccessTokenCache
{
    /// <summary>
    /// Refresh this long BEFORE the token actually expires, so a token never dies mid-request —
    /// a call that starts at expiry-minus-one-second would otherwise fail on a valid token.
    /// </summary>
    public static readonly TimeSpan RenewMargin = TimeSpan.FromMinutes(5);

    /// <summary>Assumed lifetime when Zoho omits <c>expires_in</c>. Zoho's real value is 3600.</summary>
    public const int DefaultLifetimeSeconds = 3600;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;

    private string? _token;
    private DateTimeOffset _expiresAtUtc;

    public ZohoAccessTokenCache(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Diagnostics only: how many times the refresh delegate has actually been invoked.</summary>
    public int RefreshCount { get; private set; }

    /// <summary>
    /// The cached token when it is still comfortably valid, otherwise ONE refresh shared by every
    /// concurrent caller.
    ///
    /// <para>The double-check inside the lock is the point: without it, twenty jobs waking together
    /// would queue on the semaphore and then each perform its own refresh anyway — reproducing the
    /// very burst this class exists to stop.</para>
    /// </summary>
    public async Task<string?> GetAsync(
        Func<CancellationToken, Task<ZohoTokenResult>> refresh, CancellationToken ct = default)
    {
        if (IsFresh()) return _token;

        await _gate.WaitAsync(ct);
        try
        {
            if (IsFresh()) return _token;   // another caller refreshed while we waited

            var result = await refresh(ct);
            RefreshCount++;

            if (string.IsNullOrEmpty(result.Token))
            {
                // NEVER cache a failure: the next caller must be free to try again (the credential
                // may be throttled for a moment, not broken). The alerting in §524 is throttled
                // separately, so retrying here cannot flood the operator's inbox.
                _token = null;
                _expiresAtUtc = default;
                return null;
            }

            var lifetime = result.ExpiresInSeconds > 0
                ? result.ExpiresInSeconds
                : DefaultLifetimeSeconds;

            _token = result.Token;
            _expiresAtUtc = _clock.GetUtcNow().AddSeconds(lifetime);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drop the cached token — for a caller that has just been told by Zoho that it is no longer
    /// accepted (a 401 mid-life, e.g. the grant was revoked in the Zoho console). The next
    /// <see cref="GetAsync"/> then performs a real refresh instead of handing back a dead token
    /// for the rest of its nominal hour.
    /// </summary>
    public void Invalidate()
    {
        _token = null;
        _expiresAtUtc = default;
    }

    private bool IsFresh() =>
        _token is not null && _clock.GetUtcNow() < _expiresAtUtc - RenewMargin;
}
