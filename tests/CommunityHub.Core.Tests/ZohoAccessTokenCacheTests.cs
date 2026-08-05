using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §525 — one Zoho access token, shared until it is nearly expired.
///
/// <para>The bug: <c>GetAccessTokenAsync</c> had 27 call sites and no caching, so every sync pass,
/// every page and every one of ~21 timer jobs exchanged the refresh token for a NEW access token,
/// though one lasts about an hour. Zoho rate-limits refresh grants, so the app tripped the limit in
/// bursts and every Zoho sync reported "token refresh failed" — while the same credential returned
/// HTTP 200 when tried once by hand. These tests pin the sharing that stops it.</para>
/// </summary>
public sealed class ZohoAccessTokenCacheTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public Clock(DateTimeOffset now) => Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Func<CancellationToken, Task<ZohoTokenResult>> Yields(
        string token, int expiresIn, Action? onCall = null)
        => _ => { onCall?.Invoke(); return Task.FromResult(new ZohoTokenResult(token, expiresIn)); };

    [Fact]
    public async Task The_first_call_refreshes_and_returns_the_token()
    {
        var cache = new ZohoAccessTokenCache(new Clock(T0));

        var token = await cache.GetAsync(Yields("tok-1", 3600));

        Assert.Equal("tok-1", token);
        Assert.Equal(1, cache.RefreshCount);
    }

    [Fact]
    public async Task A_second_call_reuses_the_cached_token_without_refreshing()
    {
        // THE FIX: 27 call sites must cost ONE grant, not 27.
        var cache = new ZohoAccessTokenCache(new Clock(T0));
        var calls = 0;

        for (var i = 0; i < 27; i++)
            await cache.GetAsync(Yields("tok-1", 3600, () => calls++));

        Assert.Equal(1, calls);
        Assert.Equal(1, cache.RefreshCount);
    }

    [Fact]
    public async Task An_expired_token_is_refreshed_again()
    {
        var clock = new Clock(T0);
        var cache = new ZohoAccessTokenCache(clock);

        Assert.Equal("tok-1", await cache.GetAsync(Yields("tok-1", 3600)));

        clock.Now = T0.AddHours(2);   // well past expiry
        Assert.Equal("tok-2", await cache.GetAsync(Yields("tok-2", 3600)));
        Assert.Equal(2, cache.RefreshCount);
    }

    [Fact]
    public async Task The_token_is_renewed_BEFORE_it_expires_so_it_never_dies_mid_request()
    {
        // A call starting at expiry-minus-a-second would otherwise hand out a token that dies
        // during the request. Inside the margin we must renew, not reuse.
        var clock = new Clock(T0);
        var cache = new ZohoAccessTokenCache(clock);
        await cache.GetAsync(Yields("tok-1", 3600));

        clock.Now = T0.AddSeconds(3600) - ZohoAccessTokenCache.RenewMargin.Add(TimeSpan.FromSeconds(-1));

        Assert.Equal("tok-2", await cache.GetAsync(Yields("tok-2", 3600)));
        Assert.Equal(2, cache.RefreshCount);
    }

    [Fact]
    public async Task Just_inside_the_margin_the_cached_token_is_still_reused()
    {
        var clock = new Clock(T0);
        var cache = new ZohoAccessTokenCache(clock);
        await cache.GetAsync(Yields("tok-1", 3600));

        // One second before the renew margin opens — still comfortably valid.
        clock.Now = T0.AddSeconds(3600) - ZohoAccessTokenCache.RenewMargin.Add(TimeSpan.FromSeconds(1));

        Assert.Equal("tok-1", await cache.GetAsync(Yields("tok-2", 3600)));
        Assert.Equal(1, cache.RefreshCount);
    }

    [Fact]
    public async Task A_failed_refresh_HOLDS_OFF_and_then_recovers_by_itself()
    {
        // 🔄 §783.12 REPLACES the §525 test `A_failed_refresh_is_NOT_cached_so_the_next_caller_
        // retries`, whose comment read: "Caching a failure would turn a momentary throttle into an
        // outage lasting an hour."
        //
        // 🔒 PRODUCTION INVERTED THAT REASONING. Not caching the failure is what turned a momentary
        // throttle into an outage — an unbounded one. Zoho's budget is 10 token requests per 10
        // minutes per refresh token; with ~21 timer jobs across FOUR hosts sharing one token
        // (§783.12b), "the next caller retries" means the budget is re-spent the instant it refills
        // and can never recover. PROD showed the same 401 on every tick for hours.
        //
        // The corrected contract: hold off, then recover WITHOUT human intervention. Both halves
        // matter — the hold-off is what lets the window reset, and the self-recovery is what keeps
        // it from needing a redeploy.
        var clock = new Clock(T0);
        var cache = new ZohoAccessTokenCache(clock);

        Assert.Null(await cache.GetAsync(Yields(null!, 0)));
        Assert.Equal(1, cache.RefreshCount);

        // Immediately after: refused WITHOUT touching Zoho. This is the line the old test got wrong.
        Assert.Null(await cache.GetAsync(Yields("tok-1", 3600)));
        Assert.Equal(1, cache.RefreshCount);

        // Once the window has had time to reset, the very next caller succeeds.
        clock.Now = T0 + ZohoAccessTokenCache.FailureCooldown;
        Assert.Equal("tok-1", await cache.GetAsync(Yields("tok-1", 3600)));
        Assert.Equal(2, cache.RefreshCount);
    }

    [Fact]
    public async Task A_missing_expires_in_falls_back_to_an_hour_rather_than_expiring_instantly()
    {
        // expires_in = 0 must not mean "already expired", or the cache would refresh every call
        // and reproduce the original bug exactly.
        var clock = new Clock(T0);
        var cache = new ZohoAccessTokenCache(clock);

        await cache.GetAsync(Yields("tok-1", 0));
        clock.Now = T0.AddMinutes(30);

        Assert.Equal("tok-1", await cache.GetAsync(Yields("tok-2", 0)));
        Assert.Equal(1, cache.RefreshCount);
    }

    [Fact]
    public async Task Concurrent_callers_share_ONE_refresh()
    {
        // The burst case: many jobs waking together. Without the double-check inside the lock they
        // would queue and then each refresh anyway — the very thing that tripped the rate limit.
        var cache = new ZohoAccessTokenCache(new Clock(T0));
        var calls = 0;
        var gate = new TaskCompletionSource();

        async Task<ZohoTokenResult> SlowRefresh(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return new ZohoTokenResult("tok-1", 3600);
        }

        var callers = Enumerable.Range(0, 20).Select(_ => cache.GetAsync(SlowRefresh)).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal("tok-1", r));
    }

    [Fact]
    public async Task Invalidate_forces_the_next_call_to_refresh()
    {
        // For a caller that has just been told by Zoho the token is no longer accepted.
        var cache = new ZohoAccessTokenCache(new Clock(T0));
        await cache.GetAsync(Yields("tok-1", 3600));

        cache.Invalidate();

        Assert.Equal("tok-2", await cache.GetAsync(Yields("tok-2", 3600)));
        Assert.Equal(2, cache.RefreshCount);
    }

    // ---- §783.12: a FAILED exchange must not free everyone to retry ---------
    //
    // 🔒 The PROD outage these pin: Zoho allows 10 token requests per 10 minutes. The cache used to
    // clear its state on failure so "the next caller is free to try again", and with ~21 timer jobs
    // that spends the budget the instant it refills — the retries are what keep the window empty, so
    // a transient throttle sustains itself indefinitely. Each of these fails on the old behaviour.

    private static Func<CancellationToken, Task<ZohoTokenResult>> Fails(Action? onCall = null)
        => _ => { onCall?.Invoke(); return Task.FromResult(new ZohoTokenResult(null, 0)); };

    [Fact]
    public async Task A_failed_exchange_stops_the_next_caller_from_asking_zoho_again()
    {
        var cache = new ZohoAccessTokenCache(new Clock(T0));
        var calls = 0;

        Assert.Null(await cache.GetAsync(Fails(() => calls++)));
        Assert.Equal(1, calls);

        // The whole fleet wakes up behind the failure. NONE of them may reach Zoho.
        for (var i = 0; i < 20; i++) Assert.Null(await cache.GetAsync(Fails(() => calls++)));

        Assert.Equal(1, calls);
        Assert.Equal(20, cache.SuppressedByCooldownCount);
    }

    [Fact]
    public async Task The_cooldown_lifts_on_its_own_so_a_repaired_credential_needs_no_deploy()
    {
        var clock = new Clock(T0);
        var cache = new ZohoAccessTokenCache(clock);
        await cache.GetAsync(Fails());

        // Still held one second before the cooldown expires...
        clock.Now = T0 + ZohoAccessTokenCache.FailureCooldown - TimeSpan.FromSeconds(1);
        Assert.Null(await cache.GetAsync(Yields("tok-late", 3600)));

        // ...and free the moment it passes.
        clock.Now = T0 + ZohoAccessTokenCache.FailureCooldown;
        Assert.Equal("tok-late", await cache.GetAsync(Yields("tok-late", 3600)));
    }

    [Fact]
    public async Task A_success_clears_a_standing_cooldown_and_invalidate_is_not_held_back()
    {
        var clock = new Clock(T0);
        var cache = new ZohoAccessTokenCache(clock);
        await cache.GetAsync(Fails());

        clock.Now = T0 + ZohoAccessTokenCache.FailureCooldown;
        Assert.Equal("tok-ok", await cache.GetAsync(Yields("tok-ok", 3600)));

        // Invalidate mid-life (a genuine revoke). The earlier throttle must NOT mute a legitimate
        // re-auth for 15 minutes.
        cache.Invalidate();
        Assert.Equal("tok-next", await cache.GetAsync(Yields("tok-next", 3600)));
    }

    [Fact]
    public void The_cooldown_is_never_zero_and_outlasts_zohos_own_window()
    {
        // The single most important property: any positive value works, ZERO reproduces the outage.
        Assert.True(ZohoAccessTokenCache.FailureCooldown > TimeSpan.Zero);

        // And it must outlast Zoho's own 10-minute budget window, or the retry lands inside the
        // same exhausted window it was meant to let recover.
        Assert.True(ZohoAccessTokenCache.FailureCooldown >= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task The_cooldown_reports_when_it_lifts_so_a_job_can_say_why_it_is_idle()
    {
        var clock = new Clock(T0);
        var cache = new ZohoAccessTokenCache(clock);

        Assert.Null(cache.RetryNotBeforeUtc);           // nothing has failed yet

        await cache.GetAsync(Fails());
        Assert.Equal(T0 + ZohoAccessTokenCache.FailureCooldown, cache.RetryNotBeforeUtc);

        clock.Now = T0 + ZohoAccessTokenCache.FailureCooldown;
        Assert.Null(cache.RetryNotBeforeUtc);           // expired ⇒ no longer reported
    }
}
