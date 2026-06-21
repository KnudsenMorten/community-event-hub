using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="ConfigOverrideStore"/> — the per-edition
/// override read/upsert/delete service with cache + invalidation. Uses the EF
/// in-memory provider + a real <see cref="MemoryCache"/> so the caching path is
/// exercised exactly as in production.
/// </summary>
public sealed class ConfigOverrideStoreTests
{
    private static (ConfigOverrideStore store, IMemoryCache cache) NewStore(
        CommunityHub.Core.Data.CommunityHubDbContext db, FixedClock clock)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        return (new ConfigOverrideStore(db, cache, clock), cache);
    }

    [Fact]
    public async Task No_row_yields_null_override()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var (store, _) = NewStore(db, new FixedClock(DateTimeOffset.UtcNow));

        var json = await store.GetOverrideJsonAsync(eventId, ConfigSection.Event);

        Assert.Null(json);
    }

    [Fact]
    public async Task Upsert_then_read_returns_the_fragment()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var (store, _) = NewStore(db, new FixedClock(DateTimeOffset.UtcNow));

        await store.UpsertAsync(eventId, ConfigSection.Event,
            """{ "edition": { "expectedAttendees": 500 } }""", "admin@example.test");

        var json = await store.GetOverrideJsonAsync(eventId, ConfigSection.Event);
        Assert.Contains("expectedAttendees", json);

        var row = await store.GetAsync(eventId, ConfigSection.Event);
        Assert.NotNull(row);
        Assert.Equal("admin@example.test", row!.UpdatedByEmail);
    }

    [Fact]
    public async Task Upsert_twice_updates_in_place_one_row_per_section()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var (store, _) = NewStore(db, new FixedClock(DateTimeOffset.UtcNow));

        await store.UpsertAsync(eventId, ConfigSection.Sponsor, """{ "a": 1 }""", "a@x.test");
        await store.UpsertAsync(eventId, ConfigSection.Sponsor, """{ "a": 2 }""", "b@x.test");

        var rows = db.ConfigOverrides
            .Where(o => o.EventId == eventId && o.Section == ConfigSection.Sponsor)
            .ToList();
        Assert.Single(rows);                       // upsert, not insert-again
        Assert.Contains("\"a\":2", rows[0].OverrideJson.Replace(" ", ""));
        Assert.Equal("b@x.test", rows[0].UpdatedByEmail);
    }

    [Fact]
    public async Task Sections_are_independent()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var (store, _) = NewStore(db, new FixedClock(DateTimeOffset.UtcNow));

        await store.UpsertAsync(eventId, ConfigSection.Event, """{ "e": 1 }""", null);
        await store.UpsertAsync(eventId, ConfigSection.Integrations, """{ "i": 1 }""", null);

        Assert.Contains("\"e\"", await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));
        Assert.Contains("\"i\"", await store.GetOverrideJsonAsync(eventId, ConfigSection.Integrations));
        Assert.Null(await store.GetOverrideJsonAsync(eventId, ConfigSection.Sponsor));
    }

    [Fact]
    public async Task Cache_is_invalidated_on_upsert()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var (store, _) = NewStore(db, new FixedClock(DateTimeOffset.UtcNow));

        // Prime the cache with the absence-of-override (null), then write one.
        Assert.Null(await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));

        await store.UpsertAsync(eventId, ConfigSection.Event, """{ "v": 1 }""", null);

        // If the upsert did not invalidate, this would still read the cached null.
        var after = await store.GetOverrideJsonAsync(eventId, ConfigSection.Event);
        Assert.Contains("\"v\"", after);
    }

    [Fact]
    public async Task Delete_removes_the_row_and_invalidates_cache()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var (store, _) = NewStore(db, new FixedClock(DateTimeOffset.UtcNow));

        await store.UpsertAsync(eventId, ConfigSection.Event, """{ "v": 1 }""", null);
        Assert.NotNull(await store.GetOverrideJsonAsync(eventId, ConfigSection.Event)); // primes cache

        await store.DeleteAsync(eventId, ConfigSection.Event);

        Assert.Null(await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));
        Assert.Empty(db.ConfigOverrides.Where(o => o.EventId == eventId));
    }

    [Fact]
    public async Task Blank_fragment_reads_back_as_null_override()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var (store, _) = NewStore(db, new FixedClock(DateTimeOffset.UtcNow));

        await store.UpsertAsync(eventId, ConfigSection.Event, "   ", null);

        // A blank fragment is stored but means "no override" to readers.
        Assert.Null(await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));
    }

    [Fact]
    public async Task Store_override_flows_through_the_loader_to_effective_config()
    {
        // End-to-end: a stored override changes the effective EventEditionConfig
        // the loader produces; with no row the effective value is the default.
        var path = Path.Combine(Path.GetTempPath(), $"ceh-e2e-{Guid.NewGuid():N}.json");
        File.WriteAllText(path,
            """{ "edition": { "code": "ELDK27", "expectedAttendees": 400 } }""");
        try
        {
            using var db = TestDb.New();
            var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
            var (store, _) = NewStore(db, new FixedClock(DateTimeOffset.UtcNow));
            var loader = new EventEditionConfigLoader();

            // No override yet → effective == default.
            var before = loader.Load(path,
                await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));
            Assert.Equal(400, before.ExpectedAttendees);

            // Save an override → effective reflects the merge.
            await store.UpsertAsync(eventId, ConfigSection.Event,
                """{ "edition": { "expectedAttendees": 750 } }""", "admin@example.test");

            var after = loader.Load(path,
                await store.GetOverrideJsonAsync(eventId, ConfigSection.Event));
            Assert.Equal("ELDK27", after.Code);     // untouched
            Assert.Equal(750, after.ExpectedAttendees); // overridden
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdatedAt_uses_the_injected_clock()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var when = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var (store, _) = NewStore(db, new FixedClock(when));

        await store.UpsertAsync(eventId, ConfigSection.Event, """{ "v": 1 }""", null);

        var row = await store.GetAsync(eventId, ConfigSection.Event);
        Assert.Equal(when, row!.UpdatedAt);
    }
}
