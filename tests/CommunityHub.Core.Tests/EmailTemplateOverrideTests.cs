using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §25h: per-edition email-template overrides. The store upserts/reads/clears
/// the override text (cache-invalidating), and the template→feature map only references real
/// FeatureCatalog keys so each template's ring is dialable.
/// </summary>
public sealed class EmailTemplateOverrideTests
{
    private const int EventId = 14;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-22T12:00:00Z");
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"tploverride-{Guid.NewGuid():N}").Options);

    private static EmailTemplateOverrideStore NewStore(CommunityHubDbContext db) =>
        new(db, new MemoryCache(new MemoryCacheOptions()), new FixedClock());

    [Fact]
    public async Task Upsert_then_Get_returns_text_and_Delete_resets_to_default()
    {
        using var db = NewDb();
        var store = NewStore(db);

        Assert.Null(await store.GetOverrideTextAsync(EventId, "welcome"));   // no override ⇒ default

        await store.UpsertAsync(EventId, "welcome", "Subject: Hi\n<p>edited</p>", "org@x");
        Assert.Equal("Subject: Hi\n<p>edited</p>", await store.GetOverrideTextAsync(EventId, "welcome"));

        var row = await store.GetAsync(EventId, "welcome");
        Assert.NotNull(row);
        Assert.Equal("org@x", row!.UpdatedByEmail);

        await store.DeleteAsync(EventId, "welcome");                          // reset
        Assert.Null(await store.GetOverrideTextAsync(EventId, "welcome"));    // back to default
    }

    [Fact]
    public async Task Override_is_edition_scoped()
    {
        using var db = NewDb();
        var store = NewStore(db);
        await store.UpsertAsync(EventId, "broadcast", "Subject: A\nbody", null);

        Assert.NotNull(await store.GetOverrideTextAsync(EventId, "broadcast"));
        Assert.Null(await store.GetOverrideTextAsync(EventId + 1, "broadcast"));  // other edition unaffected
    }

    [Fact]
    public void Catalog_maps_only_to_real_feature_keys()
    {
        foreach (var kv in EmailTemplateCatalog.Map)
        {
            var fk = kv.Value.FeatureKey;
            Assert.True(
                fk == FeatureCatalog.OutboundEmailKey || FeatureCatalog.Find(fk) is not null,
                $"template '{kv.Key}' maps to unknown feature key '{fk}'.");
            Assert.False(string.IsNullOrWhiteSpace(kv.Value.Title));
        }
    }
}
