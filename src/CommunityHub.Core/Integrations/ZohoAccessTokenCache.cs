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

    /// <summary>
    /// §783.12 — after a FAILED exchange, refuse to ask Zoho again for this long.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>This is the fix for a self-sustaining outage, not a politeness delay.</b> Zoho
    /// allows <b>10 access-token requests per 10 minutes</b> per refresh token
    /// (<c>zoho.com/accounts/protocol/oauth/token-limits.html</c>). The previous behaviour was to
    /// cache nothing on failure so "the next caller is free to try again" — which, with ~21 timer
    /// jobs on 5- and 10-minute cadences across two hosts, means the spent budget is hammered by
    /// every one of them, forever. The retries are what stop the window from ever refilling, so a
    /// transient throttle becomes a permanent 401. Operator 2026-08-03: <i>"the api will throw an
    /// error back when you hit the limit and then you have to wait for 1 hr"</i>.</para>
    ///
    /// <para>⚠️ <b>15 minutes, deliberately, not the full hour.</b> Long enough that the 10-minute
    /// window fully resets even with several hosts sharing one refresh token, short enough that a
    /// credential fixed in the Zoho console starts working again without a redeploy. The one thing
    /// it must never be is ZERO.</para>
    /// </remarks>
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;

    private string? _token;
    private DateTimeOffset _expiresAtUtc;
    private DateTimeOffset _retryNotBeforeUtc;

    public ZohoAccessTokenCache(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Diagnostics only: how many times the refresh delegate has actually been invoked.</summary>
    public int RefreshCount { get; private set; }

    /// <summary>
    /// Diagnostics: how many callers were refused WITHOUT asking Zoho because the §783.12 cooldown
    /// was still in force. A large number here is the loop being prevented, not a problem.
    /// </summary>
    public int SuppressedByCooldownCount { get; private set; }

    /// <summary>
    /// When the cooldown lifts, or null when there is none. Surfaced so a job can SAY why it is not
    /// talking to Zoho instead of going quietly idle (§545(b)).
    /// </summary>
    public DateTimeOffset? RetryNotBeforeUtc =>
        _retryNotBeforeUtc == default || _clock.GetUtcNow() >= _retryNotBeforeUtc
            ? null
            : _retryNotBeforeUtc;

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

        // §783.12 — checked BEFORE the lock as well, so a burst of jobs waking together is turned
        // away immediately rather than queueing up to each discover the cooldown in turn.
        if (InCooldown())
        {
            SuppressedByCooldownCount++;
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (IsFresh()) return _token;   // another caller refreshed while we waited
            if (InCooldown())
            {
                SuppressedByCooldownCount++;
                return null;
            }

            var result = await refresh(ct);
            RefreshCount++;

            if (string.IsNullOrEmpty(result.Token))
            {
                // §783.12 — HOLD OFF instead of freeing everyone to retry.
                //
                // 🔒 This line used to read "NEVER cache a failure: the next caller must be free to
                // try again". That is right for a one-off blip and catastrophically wrong for GRANT
                // THROTTLING, which is the failure Zoho actually produces here: the budget is 10
                // token requests per 10 minutes, and ~21 jobs each "free to try again" spend it the
                // instant it refills. The outage then sustains itself with no bad credential
                // anywhere — the exact PROD signature in §783.12, the same 401 every tick for hours.
                //
                // The token is still cleared (it is not usable), but the RETRY is gated.
                _token = null;
                _expiresAtUtc = default;
                _retryNotBeforeUtc = _clock.GetUtcNow() + FailureCooldown;
                return null;
            }

            var lifetime = result.ExpiresInSeconds > 0
                ? result.ExpiresInSeconds
                : DefaultLifetimeSeconds;

            _token = result.Token;
            _expiresAtUtc = _clock.GetUtcNow().AddSeconds(lifetime);
            _retryNotBeforeUtc = default;   // a success clears any standing cooldown
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

    /// <summary>§783.12 — is a post-failure cooldown still in force?</summary>
    private bool InCooldown() =>
        _retryNotBeforeUtc != default && _clock.GetUtcNow() < _retryNotBeforeUtc;
}
