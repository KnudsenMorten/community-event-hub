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

    /// <summary>
    /// §1142 — the SHARED store, so a token survives the process that minted it.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>The in-memory singleton was necessary and not sufficient, measured.</b> A
    /// singleton lives as long as its process; the PROD jobs host ran <b>5,520 distinct instances
    /// in 24 hours</b>. Every cold instance that touched Zoho minted its own token, and Zoho keeps
    /// only <b>10 active access tokens per refresh token</b>, evicting the oldest — so tokens were
    /// retired out from under instances still holding them. §525's intent was always "one token";
    /// this is what makes that true across a fleet instead of within one process.</para>
    ///
    /// <para>🔒 Optional. Null keeps the exact pre-§1142 behaviour, so tests and every legacy
    /// construction are unchanged — and a host with no database still works, just per-process.</para>
    /// </remarks>
    private readonly IZohoTokenStore? _store;

    /// <summary>Identifies the credential the shared row belongs to (§1142).</summary>
    private readonly string _credentialKey;

    /// <summary>
    /// How long one instance may hold the right to refresh before others may take it. Long enough
    /// for a token exchange, short enough that an instance dying mid-refresh — routine on a host
    /// recycling hundreds of times an hour — does not wedge the fleet.
    /// </summary>
    public static readonly TimeSpan RefreshLease = TimeSpan.FromSeconds(30);

    /// <summary>How long a caller that LOST the refresh race waits before re-reading the row.</summary>
    public static readonly TimeSpan LoserWait = TimeSpan.FromMilliseconds(750);

    private string? _token;
    private DateTimeOffset _expiresAtUtc;
    private DateTimeOffset _retryNotBeforeUtc;

    public ZohoAccessTokenCache(
        TimeProvider? clock = null, IZohoTokenStore? store = null, string? credentialKey = null)
    {
        _clock = clock ?? TimeProvider.System;
        _store = store;
        _credentialKey = string.IsNullOrWhiteSpace(credentialKey) ? "default" : credentialKey!;
    }

    /// <summary>
    /// §1142 — a stable, non-reversible id for a credential pair. Rotating the credential changes
    /// the key, which orphans the old row rather than serving a token minted from a credential that
    /// no longer exists.
    /// </summary>
    /// <remarks>🔒 Hashed, so the refresh token never appears in a database column, a log line or a
    /// query plan.</remarks>
    public static string CredentialKeyFor(string? clientId, string? refreshToken)
    {
        var material = (clientId ?? string.Empty) + "\n" + (refreshToken ?? string.Empty);
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(bytes)[..32];
    }

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

            // ── §1142 — ASK THE FLEET BEFORE ASKING ZOHO ──────────────────────────────────
            //
            // 🔑 This block is the fix. A cold instance now INHERITS the live token instead of
            // minting one, which is what makes "one token" true across 5,520 instances a day
            // rather than only within one process.
            //
            // 🔒 Every store call is fail-soft: if the database is unreachable we fall through to
            // the pre-§1142 behaviour rather than taking Zoho down over a storage blip. A degraded
            // token cache is an inefficiency; a dead one is an outage.
            if (_store is not null)
            {
                var shared = await TryReadSharedAsync(ct);

                if (shared is { } s1)
                {
                    if (AdoptIfFresh(s1)) return _token;         // somebody else's token, still good
                    if (SharedCooldownInForce(s1))
                    {
                        SuppressedByCooldownCount++;
                        AdoptedCooldownCount++;
                        return null;
                    }
                }

                // Nothing usable — exactly one instance may now talk to Zoho.
                var now = _clock.GetUtcNow();
                var claimed = await TryClaimAsync(now + RefreshLease, now, ct);

                if (!claimed)
                {
                    // ⚠️ We LOST the race, and that is the good outcome: refreshing anyway is the
                    // stampede. Wait for the winner, then take what it wrote.
                    LostRefreshRaceCount++;
                    await Task.Delay(LoserWait, _clock, ct);

                    var after = await TryReadSharedAsync(ct);
                    if (after is { } s2)
                    {
                        if (AdoptIfFresh(s2)) return _token;
                        if (SharedCooldownInForce(s2))
                        {
                            SuppressedByCooldownCount++;
                            return null;
                        }
                    }

                    // The winner has not finished (or died). Do NOT refresh as a consolation —
                    // that is the stampede arriving late. The caller retries on its next tick.
                    return null;
                }
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

                // §1142 — SHARE THE COOLDOWN. One instance discovering a throttle must stop every
                // other instance from spending the budget discovering it too. Per-process
                // cooldowns × hundreds of instances is not a cooldown at all.
                await TryWriteCooldownAsync(_retryNotBeforeUtc, ct);
                return null;
            }

            var lifetime = result.ExpiresInSeconds > 0
                ? result.ExpiresInSeconds
                : DefaultLifetimeSeconds;

            _token = result.Token;
            _expiresAtUtc = _clock.GetUtcNow().AddSeconds(lifetime);
            _retryNotBeforeUtc = default;   // a success clears any standing cooldown

            // §1142 — publish it, so the next cold instance inherits this token instead of
            // minting another one against a 10-active-token cap.
            await TryWriteTokenAsync(_token!, _expiresAtUtc, ct);
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

    /// <summary>
    /// §1141 — drop the cached token ONLY if it is still the one that just failed.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>This is the storm-safe form of <see cref="Invalidate"/>, and the reason the
    /// unconditional one must not be used on a 401.</b> Several jobs run concurrently across two
    /// hosts and all hold the same access token. When Zoho rejects it, each of them arrives here in
    /// turn. Unconditional invalidation means caller A clears the token, refreshes, and gets a good
    /// one — and then caller B, still holding the DEAD token, clears the good one again. With ~21
    /// timer jobs that is a refresh per job, which spends Zoho's budget of <b>10 token requests per
    /// 10 minutes</b> and converts a transient rejection into the sustained outage §783.12 exists to
    /// prevent.</para>
    ///
    /// <para>🔑 Comparing first makes the operation idempotent: the first caller replaces the dead
    /// token, every later caller finds its dead token is no longer the cached one, does nothing, and
    /// simply picks up the new token. <b>One refresh per rejection, not one per caller.</b></para>
    ///
    /// <para>⚠️ Returns false when nothing was invalidated — the caller must then re-read the cache
    /// rather than assume its own token is still current.</para>
    /// </remarks>
    public bool InvalidateIfCurrent(string? token)
    {
        if (token is null || _token is null || !string.Equals(_token, token, StringComparison.Ordinal))
            return false;

        _token = null;
        _expiresAtUtc = default;
        RejectedTokenCount++;
        return true;
    }

    /// <summary>
    /// Diagnostics: how many times a cached token was dropped because Zoho rejected it mid-life.
    /// Persistently non-zero means tokens are dying before their nominal hour — with DEV and PROD
    /// sharing one refresh token (§783.12b), the usual cause is another host refreshing.
    /// </summary>
    public int RejectedTokenCount { get; private set; }

    // ── §1142 — shared-store helpers. ALL fail-soft, by design. ─────────────────────────────
    //
    // 🔒 A store outage must degrade this class to its pre-§1142 behaviour, never break it. The
    // shared row is an optimisation over "every instance mints its own"; losing it costs token
    // requests, while throwing here would cost the entire Zoho integration.

    /// <summary>Diagnostics: callers that correctly deferred to another instance's refresh.</summary>
    public int LostRefreshRaceCount { get; private set; }

    /// <summary>Diagnostics: times a cooldown set by ANOTHER instance was honoured here.</summary>
    public int AdoptedCooldownCount { get; private set; }

    /// <summary>Diagnostics: tokens adopted from the shared row instead of being minted.</summary>
    public int AdoptedFromStoreCount { get; private set; }

    /// <summary>Diagnostics: shared-store operations that failed and were swallowed.</summary>
    public int StoreFailureCount { get; private set; }

    private async Task<ZohoSharedToken?> TryReadSharedAsync(CancellationToken ct)
    {
        try { return await _store!.ReadAsync(_credentialKey, ct); }
        catch (OperationCanceledException) { throw; }
        catch { StoreFailureCount++; return null; }
    }

    private async Task<bool> TryClaimAsync(
        DateTimeOffset leaseUntil, DateTimeOffset now, CancellationToken ct)
    {
        try { return await _store!.TryClaimRefreshAsync(_credentialKey, leaseUntil, now, ct); }
        catch (OperationCanceledException) { throw; }
        catch
        {
            StoreFailureCount++;
            // ⚠️ Fail OPEN on a claim failure: if the store cannot arbitrate, this instance must
            // still be able to get a token. Better a duplicate grant than a dead integration.
            return true;
        }
    }

    private async Task TryWriteTokenAsync(string token, DateTimeOffset expiresAt, CancellationToken ct)
    {
        if (_store is null) return;
        try { await _store.WriteTokenAsync(_credentialKey, token, expiresAt, ct); }
        catch (OperationCanceledException) { throw; }
        catch { StoreFailureCount++; }
    }

    private async Task TryWriteCooldownAsync(DateTimeOffset retryNotBefore, CancellationToken ct)
    {
        if (_store is null) return;
        try { await _store.WriteCooldownAsync(_credentialKey, retryNotBefore, ct); }
        catch (OperationCanceledException) { throw; }
        catch { StoreFailureCount++; }
    }

    /// <summary>Adopt a shared token into memory when it is comfortably valid.</summary>
    private bool AdoptIfFresh(ZohoSharedToken shared)
    {
        if (string.IsNullOrEmpty(shared.AccessToken)) return false;
        if (_clock.GetUtcNow() >= shared.ExpiresAtUtc - RenewMargin) return false;

        _token = shared.AccessToken;
        _expiresAtUtc = shared.ExpiresAtUtc;
        _retryNotBeforeUtc = default;
        AdoptedFromStoreCount++;
        return true;
    }

    private bool SharedCooldownInForce(ZohoSharedToken shared) =>
        shared.RetryNotBeforeUtc is { } until && _clock.GetUtcNow() < until;

    private bool IsFresh() =>
        _token is not null && _clock.GetUtcNow() < _expiresAtUtc - RenewMargin;

    /// <summary>§783.12 — is a post-failure cooldown still in force?</summary>
    private bool InCooldown() =>
        _retryNotBeforeUtc != default && _clock.GetUtcNow() < _retryNotBeforeUtc;
}
