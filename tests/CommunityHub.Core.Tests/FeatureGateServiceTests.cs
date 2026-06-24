using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the §23 gate. The EF Core InMemory provider runs the real
/// DbContext mapping + LINQ. They prove the resolution order (persisted switch ⇒
/// catalog default), that disabling is honoured, the combined "all must be on"
/// helper, edition scoping, and the settings-service upsert + dependency
/// detection. Together with the job/service gate tests this is the behavioural
/// half of the release gate: a disabled advanced feature resolves to false, so a
/// caller no-ops.
/// </summary>
public sealed class FeatureGateServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"featgate-{Guid.NewGuid():N}")
            .Options);

    private static FeatureGateService NewGate(CommunityHubDbContext db) => new(db);
    private static FeatureSettingsService NewSettings(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now));

    [Fact]
    public async Task Advanced_feature_with_no_persisted_row_falls_back_to_catalog_default_off()
    {
        using var db = NewDb();
        var gate = NewGate(db);

        // backstage-sync defaults OFF in the catalog and nothing is persisted.
        Assert.False(await gate.IsFeatureEnabledAsync("backstage-sync", EventId));
    }

    [Theory]
    [InlineData("sponsor-order-pull")]
    [InlineData("sponsor-leads")]
    [InlineData("sponsor-upload-watch")]
    [InlineData("attendee-reconcile")]
    public async Task New_residual_sync_jobs_default_off_and_honour_the_persisted_switch(string key)
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        var gate = NewGate(db);

        // Each new advanced sync feature defaults OFF (opt-in) — the job/web trigger no-ops.
        Assert.False(await gate.IsFeatureEnabledAsync(key, EventId));

        // The organizer's per-edition switch turns it on; a different edition stays OFF.
        await settings.SetEnabledAsync(EventId, key, true, "org@expertslive.dk");
        Assert.True(await gate.IsFeatureEnabledAsync(key, EventId));
        Assert.False(await gate.IsFeatureEnabledAsync(key, OtherEventId));
    }

    [Fact]
    public async Task Email_master_switch_defaults_on()
    {
        using var db = NewDb();
        var gate = NewGate(db);
        Assert.True(await gate.IsOutboundEmailEnabledAsync(EventId));
    }

    [Fact]
    public async Task Jobs_pause_master_switch_defaults_running_and_round_trips_per_edition()
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        var gate = NewGate(db);

        // Default (no row): NOT paused — jobs run, so behaviour is unchanged.
        Assert.False(await gate.AreJobsPausedAsync(EventId));

        // Pause this edition only; a different edition is unaffected.
        await settings.SetJobsPausedAsync(EventId, true, "org@expertslive.dk");
        Assert.True(await gate.AreJobsPausedAsync(EventId));
        Assert.False(await gate.AreJobsPausedAsync(OtherEventId));

        // Resume.
        await settings.SetJobsPausedAsync(EventId, false, "org@expertslive.dk");
        Assert.False(await gate.AreJobsPausedAsync(EventId));
    }

    [Fact]
    public void Jobs_pause_key_is_not_a_catalog_feature()
    {
        // The pause switch is a reserved operational key, NOT a rollout-staged
        // feature — it must not appear in the catalog grid.
        Assert.Null(FeatureCatalog.Find(FeatureGateService.JobsPausedKey));
    }

    [Fact]
    public async Task Persisted_switch_wins_over_catalog_default()
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        var gate = NewGate(db);

        // Turn an OFF-by-default feature ON for this edition.
        await settings.SetEnabledAsync(EventId, "backstage-sync", true, "org@expertslive.dk");
        Assert.True(await gate.IsFeatureEnabledAsync("backstage-sync", EventId));

        // Turn the ON-by-default email master switch OFF — the persisted false wins.
        await settings.SetEnabledAsync(EventId, FeatureCatalog.OutboundEmailKey, false, "org@expertslive.dk");
        Assert.False(await gate.IsOutboundEmailEnabledAsync(EventId));
    }

    [Fact]
    public async Task Switch_is_edition_scoped()
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        var gate = NewGate(db);

        await settings.SetEnabledAsync(EventId, "some-scheduling", true, null);

        Assert.True(await gate.IsFeatureEnabledAsync("some-scheduling", EventId));
        // A different edition still sees the catalog default (OFF).
        Assert.False(await gate.IsFeatureEnabledAsync("some-scheduling", OtherEventId));
    }

    [Fact]
    public async Task AreAllEnabled_requires_every_key()
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        var gate = NewGate(db);

        // digest-emails needs reminder-jobs AND the email master switch.
        await settings.SetEnabledAsync(EventId, "digest-emails", true, null);
        // reminder-jobs still OFF -> combined gate is false.
        Assert.False(await gate.AreAllEnabledAsync(
            EventId, default, "digest-emails", "reminder-jobs"));

        await settings.SetEnabledAsync(EventId, "reminder-jobs", true, null);
        Assert.True(await gate.AreAllEnabledAsync(
            EventId, default, "digest-emails", "reminder-jobs", FeatureCatalog.OutboundEmailKey));

        // Kill the email master switch -> the combined gate fails again.
        await settings.SetEnabledAsync(EventId, FeatureCatalog.OutboundEmailKey, false, null);
        Assert.False(await gate.AreAllEnabledAsync(
            EventId, default, "digest-emails", "reminder-jobs", FeatureCatalog.OutboundEmailKey));
    }

    [Fact]
    public async Task Setting_a_core_or_unknown_key_is_ignored()
    {
        using var db = NewDb();
        var settings = NewSettings(db);

        // No core features exist in the catalog today, but an unknown key must be
        // a safe no-op that persists nothing.
        await settings.SetEnabledAsync(EventId, "totally-unknown", false, null);
        Assert.Empty(db.FeatureSettings);
    }

    [Fact]
    public async Task SetEnabled_upserts_a_single_row_per_edition_feature()
    {
        using var db = NewDb();
        var settings = NewSettings(db);

        await settings.SetEnabledAsync(EventId, "surveys", true, "a@expertslive.dk");
        await settings.SetEnabledAsync(EventId, "surveys", false, "b@expertslive.dk");

        var rows = await db.FeatureSettings
            .Where(f => f.EventId == EventId && f.FeatureKey == "surveys").ToListAsync();
        Assert.Single(rows);
        Assert.False(rows[0].Enabled);
        Assert.Equal("b@expertslive.dk", rows[0].LastUpdatedByEmail);
    }

    [Fact]
    public async Task GetByGroup_returns_every_catalog_feature_grouped_in_display_order()
    {
        using var db = NewDb();
        var settings = NewSettings(db);

        var grouped = await settings.GetByGroupAsync(EventId);
        var keys = grouped.SelectMany(g => g).Select(s => s.Key).ToHashSet();
        Assert.Equal(FeatureCatalog.All.Select(f => f.Key).ToHashSet(), keys);

        var order = grouped.Select(g => (int)g.Key).ToList();
        Assert.Equal(order.OrderBy(x => x).ToList(), order);
    }

    [Fact]
    public async Task GetByGroup_reflects_effective_state_persisted_over_default()
    {
        using var db = NewDb();
        var settings = NewSettings(db);

        await settings.SetEnabledAsync(EventId, "sessionize-import", true, null);
        var all = await settings.GetAllAsync(EventId);

        var sezimport = all.Single(s => s.Key == "sessionize-import");
        Assert.True(sezimport.Enabled);
        Assert.True(sezimport.IsPersisted);

        var backstage = all.Single(s => s.Key == "backstage-sync");
        Assert.False(backstage.Enabled);     // catalog default
        Assert.False(backstage.IsPersisted); // no row
    }

    [Fact]
    public async Task Unmet_dependency_is_flagged_when_an_enabled_feature_lacks_its_prerequisite()
    {
        using var db = NewDb();
        var settings = NewSettings(db);

        // Enable digest-emails but leave reminder-jobs OFF (a prerequisite).
        await settings.SetEnabledAsync(EventId, "digest-emails", true, null);
        var unmet = await settings.GetUnmetDependenciesAsync(EventId);

        Assert.Contains(unmet, u =>
            u.Feature.Key == "digest-emails" && u.Missing.Key == "reminder-jobs");
    }
}
