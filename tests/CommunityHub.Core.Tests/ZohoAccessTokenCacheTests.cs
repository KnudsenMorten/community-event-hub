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
    public async Task A_failed_refresh_is_NOT_cached_so_the_next_caller_retries()
    {
        // Caching a failure would turn a momentary throttle into an outage lasting an hour.
        var cache = new ZohoAccessTokenCache(new Clock(T0));

        Assert.Null(await cache.GetAsync(Yields(null!, 0)));
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
}
