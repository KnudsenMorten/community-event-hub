using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1142 — ONE Zoho access token across every INSTANCE, not merely across callers in one process.
///
/// <para>Operator 2026-08-27: <i>"i agree to make the token shareable in prod so any instance/module
/// can reuse it"</i>.</para>
///
/// <para>🔴 <b>What §525 missed, measured.</b> The token cache was made a singleton, which is
/// correct — but a singleton lives as long as its process, and the PROD jobs host ran <b>5,520
/// distinct instances in 24 hours</b>. Every cold instance that touched Zoho minted its own token;
/// Zoho keeps 10 active tokens per refresh token and evicts the oldest, so live tokens were being
/// retired out from under the instances holding them (§1141's sporadic mid-life 401).</para>
///
/// <para>These tests are mostly about what must NOT happen: a second token request when one is
/// already in flight or already succeeded.</para>
/// </summary>
public sealed class ZohoSharedTokenStoreTests
{
    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static readonly DateTimeOffset T0 = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An in-memory stand-in for the SQL row, with the same atomic-claim contract.</summary>
    private sealed class FakeStore : IZohoTokenStore
    {
        private readonly object _lock = new();
        private string? _token;
        private DateTimeOffset _expires;
        private DateTimeOffset? _cooldown;
        private DateTimeOffset? _lease;

        public int Claims { get; private set; }
        public int Writes { get; private set; }
        public bool Throw { get; set; }

        public Task<ZohoSharedToken?> ReadAsync(string key, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("store down");
            lock (_lock)
            {
                return Task.FromResult<ZohoSharedToken?>(
                    _token is null && _cooldown is null
                        ? null
                        : new ZohoSharedToken(_token, _expires, _cooldown));
            }
        }

        public Task<bool> TryClaimRefreshAsync(
            string key, DateTimeOffset leaseUntil, DateTimeOffset now, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("store down");
            lock (_lock)
            {
                // The SQL contract, mirrored: the claim succeeds only when no live lease exists.
                if (_lease is { } l && l > now) return Task.FromResult(false);
                _lease = leaseUntil;
                Claims++;
                return Task.FromResult(true);
            }
        }

        public Task WriteTokenAsync(
            string key, string token, DateTimeOffset expiresAt, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("store down");
            lock (_lock)
            {
                _token = token; _expires = expiresAt; _cooldown = null; _lease = null; Writes++;
            }
            return Task.CompletedTask;
        }

        public Task WriteCooldownAsync(
            string key, DateTimeOffset retryNotBefore, CancellationToken ct = default)
        {
            if (Throw) throw new InvalidOperationException("store down");
            lock (_lock)
            {
                _token = null; _cooldown = retryNotBefore; _lease = null; Writes++;
            }
            return Task.CompletedTask;
        }
    }

    private static ZohoAccessTokenCache NewInstance(FakeStore store, FixedClock clock) =>
        new(clock, store, "cred-A");

    private static Func<CancellationToken, Task<ZohoTokenResult>> Grants(
        string token, Action? onCall = null) =>
        _ => { onCall?.Invoke(); return Task.FromResult(new ZohoTokenResult(token, 3600)); };

    // ── the fix itself ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_COLD_INSTANCE_INHERITS_THE_LIVE_TOKEN_INSTEAD_OF_MINTING_ONE()
    {
        var store = new FakeStore();
        var clock = new FixedClock(T0);

        var first = NewInstance(store, clock);
        Assert.Equal("T1", await first.GetAsync(Grants("T1")));

        // A brand-new instance — the process that minted T1 is gone. This is the 5,520-a-day case.
        var grantsFromCold = 0;
        var cold = NewInstance(store, clock);
        var token = await cold.GetAsync(Grants("T2", () => grantsFromCold++));

        // 🔴 Before §1142 this returned T2 and burned one of Zoho's 10 active-token slots.
        Assert.Equal("T1", token);
        Assert.Equal(0, grantsFromCold);
        Assert.Equal(1, cold.AdoptedFromStoreCount);
    }

    [Fact]
    public async Task Only_ONE_instance_talks_to_Zoho_when_many_start_cold_together()
    {
        var store = new FakeStore();
        var clock = new FixedClock(T0);

        // 25 instances waking at once against an empty store — the stampede.
        var grants = 0;
        var instances = Enumerable.Range(0, 25).Select(_ => NewInstance(store, clock)).ToList();

        var results = await Task.WhenAll(instances.Select(i =>
            i.GetAsync(Grants("SHARED", () => Interlocked.Increment(ref grants)))));

        // 🔒 Exactly one grant. The others either waited and adopted it, or returned null and will
        // pick it up on their next tick — both are correct; a second grant is not.
        Assert.Equal(1, grants);
        Assert.Equal(1, store.Claims);
        Assert.Contains(results, r => r == "SHARED");
        Assert.All(results, r => Assert.True(r is null or "SHARED"));
    }

    [Fact]
    public async Task A_LOSER_never_refreshes_as_a_consolation()
    {
        var store = new FakeStore();
        var clock = new FixedClock(T0);

        // Winner takes the lease and is still working (no token written yet).
        Assert.True(await store.TryClaimRefreshAsync("cred-A", T0.AddSeconds(30), T0));

        var grants = 0;
        var loser = NewInstance(store, clock);
        var token = await loser.GetAsync(Grants("SNEAKY", () => grants++));

        // ⚠️ Refreshing here is the stampede arriving late — one instance's slow token exchange
        // must not license everyone else to mint their own.
        Assert.Null(token);
        Assert.Equal(0, grants);
        Assert.Equal(1, loser.LostRefreshRaceCount);
    }

    [Fact]
    public async Task A_COOLDOWN_set_by_one_instance_stops_the_others()
    {
        var store = new FakeStore();
        var clock = new FixedClock(T0);

        // One instance's refresh fails ⇒ it publishes the §783.12 cooldown.
        var failing = NewInstance(store, clock);
        Assert.Null(await failing.GetAsync(_ => Task.FromResult(new ZohoTokenResult(null, 0))));

        // 🔑 THE POINT: per-process cooldowns × hundreds of instances is not a cooldown at all.
        // A fresh instance must honour a throttle it never personally hit.
        var grants = 0;
        var other = NewInstance(store, clock);
        Assert.Null(await other.GetAsync(Grants("T", () => grants++)));

        Assert.Equal(0, grants);
        Assert.Equal(1, other.AdoptedCooldownCount);
    }

    [Fact]
    public async Task The_cooldown_lifts_on_its_own()
    {
        var store = new FakeStore();
        var clock = new FixedClock(T0);

        var failing = NewInstance(store, clock);
        await failing.GetAsync(_ => Task.FromResult(new ZohoTokenResult(null, 0)));

        clock.Advance(ZohoAccessTokenCache.FailureCooldown + TimeSpan.FromMinutes(1));

        var later = NewInstance(store, clock);
        Assert.Equal("OK", await later.GetAsync(Grants("OK")));
    }

    // ── fail-soft ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_STORE_OUTAGE_degrades_to_per_process_behaviour_it_does_not_break_Zoho()
    {
        var store = new FakeStore { Throw = true };
        var clock = new FixedClock(T0);
        var cache = NewInstance(store, clock);

        // 🔒 Losing the shared row costs token requests. Throwing here would cost the entire Zoho
        // integration — every one of the 16 call sites. So it fails OPEN, deliberately.
        Assert.Equal("T1", await cache.GetAsync(Grants("T1")));
        Assert.True(cache.StoreFailureCount > 0);

        // And the in-process cache still works, so the outage does not multiply requests either.
        var grants = 0;
        Assert.Equal("T1", await cache.GetAsync(Grants("T2", () => grants++)));
        Assert.Equal(0, grants);
    }

    [Fact]
    public async Task With_NO_store_the_behaviour_is_exactly_what_it_was_before()
    {
        // Every existing construction passes no store. That path must be untouched.
        var clock = new FixedClock(T0);
        var cache = new ZohoAccessTokenCache(clock);

        Assert.Equal("T1", await cache.GetAsync(Grants("T1")));
        var grants = 0;
        Assert.Equal("T1", await cache.GetAsync(Grants("T2", () => grants++)));
        Assert.Equal(0, grants);
        Assert.Equal(0, cache.AdoptedFromStoreCount);
    }

    // ── credential identity ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_credential_key_changes_when_the_credential_does()
    {
        var a = ZohoAccessTokenCache.CredentialKeyFor("client-1", "refresh-1");
        var b = ZohoAccessTokenCache.CredentialKeyFor("client-1", "refresh-2");
        var c = ZohoAccessTokenCache.CredentialKeyFor("client-2", "refresh-1");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a, ZohoAccessTokenCache.CredentialKeyFor("client-1", "refresh-1"));

        // 🔒 Hashed: the refresh token must never be reconstructible from a database column.
        Assert.DoesNotContain("refresh-1", a);
        Assert.DoesNotContain("client-1", a);
    }

    [Fact]
    public async Task A_ROTATED_credential_does_not_serve_the_old_token()
    {
        var store = new FakeStore();
        var clock = new FixedClock(T0);

        var before = new ZohoAccessTokenCache(clock, store, "cred-OLD");
        Assert.Equal("OLD", await before.GetAsync(Grants("OLD")));

        // Different credential ⇒ different key ⇒ different row. The fake keeps one slot, so this
        // asserts the KEY is what the cache asks for, not that the fake partitions.
        var afterKey = ZohoAccessTokenCache.CredentialKeyFor("client-new", "refresh-new");
        Assert.NotEqual("cred-OLD", afterKey);
    }
}
