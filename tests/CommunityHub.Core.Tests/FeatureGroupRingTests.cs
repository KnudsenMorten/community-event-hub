using CommunityHub.Core.Data;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §23a — the group-ring lifecycle model. Proves the gate's release-ring
/// resolution order (per-feature override ⇒ effective-group ring ⇒ catalog default),
/// group re-homing ("graduate"), override clear ("adopt group"), and the group-ring
/// state surface the Rollout GUI renders. EF InMemory runs the real DbContext mapping.
/// </summary>
public sealed class FeatureGroupRingTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"grpring-{Guid.NewGuid():N}")
            .Options);

    private static FeatureGateService NewGate(CommunityHubDbContext db) => new(db);
    private static FeatureSettingsService NewSettings(CommunityHubDbContext db) => new(db, new FixedClock(Now));

    // "surveys" — group Surveys, catalog default Broad. Good neutral test feature.

    [Fact]
    public async Task No_row_no_group_ring_resolves_to_catalog_default()
    {
        // §23a: existing features are pinned to Ring1 (controlled-rollout default).
        using var db = NewDb();
        Assert.Equal(Ring.Ring1, await NewGate(db).GetReleasedRingAsync("surveys", EventId));
    }

    [Fact]
    public async Task Group_ring_is_inherited_when_no_override()
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        await settings.SetGroupRingAsync(EventId, FeatureGroup.EventSettings, Ring.Ring1, "o@x.test");

        Assert.Equal(Ring.Ring1, await NewGate(db).GetReleasedRingAsync("surveys", EventId));
    }

    [Fact]
    public async Task Per_feature_override_beats_the_group_ring()
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        await settings.SetGroupRingAsync(EventId, FeatureGroup.EventSettings, Ring.Ring1, null);
        await settings.SetReleasedRingAsync(EventId, "surveys", Ring.Ring0, null); // override

        Assert.Equal(Ring.Ring0, await NewGate(db).GetReleasedRingAsync("surveys", EventId));
    }

    [Fact]
    public async Task Clearing_the_override_re_adopts_the_group_ring()
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        // §695 — "surveys" now lives in EventSettings; the four MECHANISM groups (Email, Reminders,
        // Social media, Surveys) were emptied so every switch is filed per ROLE or under Event
        // settings. The MECHANISM under test is unchanged: clearing a per-feature override re-adopts
        // whatever ring its HOME group carries.
        await settings.SetGroupRingAsync(EventId, FeatureGroup.EventSettings, Ring.Ring2, null);
        await settings.SetReleasedRingAsync(EventId, "surveys", Ring.Ring0, null);
        Assert.Equal(Ring.Ring0, await NewGate(db).GetReleasedRingAsync("surveys", EventId));

        var effective = await settings.ClearReleasedRingOverrideAsync(EventId, "surveys", null);
        Assert.Equal(Ring.Ring2, effective);
        Assert.Equal(Ring.Ring2, await NewGate(db).GetReleasedRingAsync("surveys", EventId));
    }

    [Fact]
    public async Task Re_homing_a_feature_makes_it_adopt_the_destination_group_ring()
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        // EventSettings group set to Ring0; move surveys into it ⇒ surveys becomes Ring0.
        // (§700 — was Incubation, which no longer exists. The MECHANISM under test is
        // unchanged, and it is the exact hazard §694.4 warns about: a feature with no
        // per-feature override adopts its destination group's ring when re-homed.)
        await settings.SetGroupRingAsync(EventId, FeatureGroup.EventSettings, Ring.Ring0, null);
        await settings.SetFeatureGroupAsync(EventId, "surveys", FeatureGroup.EventSettings, null);

        Assert.Equal(Ring.Ring0, await NewGate(db).GetReleasedRingAsync("surveys", EventId));

        // And it now lists under the Event settings group in the GUI grouping.
        var grouped = await settings.GetByGroupAsync(EventId);
        var destination = grouped.FirstOrDefault(g => g.Key == FeatureGroup.EventSettings);
        Assert.NotNull(destination);
        Assert.Contains(destination!, s => s.Key == "surveys");
    }

    [Fact]
    public async Task Re_homing_back_to_the_catalog_group_clears_the_override()
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        // §695 — "surveys" was re-homed OUT of the emptied Surveys mechanism group; EventSettings is
        // its catalog home now. Moving it away and back must still clear the override.
        await settings.SetFeatureGroupAsync(EventId, "surveys", FeatureGroup.Sponsors, null);
        await settings.SetFeatureGroupAsync(EventId, "surveys", FeatureGroup.EventSettings, null); // home

        var all = await settings.GetAllAsync(EventId);
        var surveys = all.Single(s => s.Key == "surveys");
        Assert.Equal(FeatureGroup.EventSettings, surveys.EffectiveGroup);
        Assert.False(surveys.IsReHomed);
    }

    [Fact]
    public async Task SetReleasedRing_records_an_override_visible_in_state()
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        await settings.SetReleasedRingAsync(EventId, "surveys", Ring.Ring1, null);

        var s = (await settings.GetAllAsync(EventId)).Single(x => x.Key == "surveys");
        Assert.True(s.IsRingOverridden);
        Assert.Equal(Ring.Ring1, s.ReleasedToRing);
        Assert.Equal(Ring.Ring1, s.OverrideRing);
    }

    [Fact]
    public async Task GetGroupRings_returns_every_group_with_defaults()
    {
        using var db = NewDb();
        var settings = NewSettings(db);
        await settings.SetGroupRingAsync(EventId, FeatureGroup.Sponsors, Ring.Ring1, null);

        var groups = await settings.GetGroupRingsAsync(EventId);
        Assert.Equal(Enum.GetValues<FeatureGroup>().Length, groups.Count);

        // Defaults from FeatureCatalog.GroupDefaultRing where unset: EVERY group is
        // Ring1 — operator 2026-06-21 "default is ring 1".
        // 🔒 §700 — assert this for EVERY group, not a hand-picked three. Uniformity is
        // what makes re-homing audience-neutral, so it is the property worth pinning; a
        // future group added with a different default must fail here, loudly.
        foreach (var g in Enum.GetValues<FeatureGroup>().Where(x => x != FeatureGroup.Sponsors))
        {
            Assert.Equal(Ring.Ring1, groups.Single(x => x.Group == g).Ring);
        }
        // The one we set is persisted.
        var sponsors = groups.Single(g => g.Group == FeatureGroup.Sponsors);
        Assert.Equal(Ring.Ring1, sponsors.Ring);
        Assert.True(sponsors.IsPersisted);
    }

    [Fact]
    public async Task Existing_outbound_email_default_ring_is_unchanged_under_the_group_model()
    {
        // Back-compat: with nothing set, outbound-email still resolves to its pinned
        // catalog default (Ring1) — the email gate behaviour is preserved.
        using var db = NewDb();
        Assert.Equal(Ring.Ring1, await NewGate(db).GetReleasedRingAsync("outbound-email", EventId));
    }
}
