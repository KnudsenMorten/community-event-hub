using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for <see cref="BoothTierRanking"/> (REQUIREMENTS §7 "Populate
/// SponsorInfo.Tier from the product classification"). This pure helper is the
/// single authority the order-pull uses to pick a company's HIGHEST booth tier
/// across its line items, and the raise-only rule that keeps the pull from ever
/// downgrading an organizer's manual tier correction. Proving it here covers the
/// decision logic; the thin SponsorInfo upsert that consumes it is exercised by
/// the live data/Playwright gate (the full pull has un-mockable HTTP deps).
/// </summary>
public sealed class BoothTierRankingTests
{
    [Theory]
    [InlineData(BoothTier.Platinum, 4)]
    [InlineData(BoothTier.Diamond, 3)]
    [InlineData(BoothTier.Gold, 2)]
    [InlineData(BoothTier.Feature, 1)]
    [InlineData(BoothTier.None, 0)]
    public void Weight_orders_tiers_most_prestigious_first(BoothTier tier, int expected)
        => Assert.Equal(expected, BoothTierRanking.Weight(tier));

    [Fact]
    public void Weight_ranks_platinum_above_diamond_above_gold_above_feature_above_none()
    {
        Assert.True(BoothTierRanking.Weight(BoothTier.Platinum) > BoothTierRanking.Weight(BoothTier.Diamond));
        Assert.True(BoothTierRanking.Weight(BoothTier.Diamond) > BoothTierRanking.Weight(BoothTier.Gold));
        Assert.True(BoothTierRanking.Weight(BoothTier.Gold) > BoothTierRanking.Weight(BoothTier.Feature));
        Assert.True(BoothTierRanking.Weight(BoothTier.Feature) > BoothTierRanking.Weight(BoothTier.None));
    }

    [Fact]
    public void HighestOf_empty_is_none()
        => Assert.Equal(BoothTier.None, BoothTierRanking.HighestOf(System.Array.Empty<BoothTier>()));

    [Fact]
    public void HighestOf_all_none_is_none()
        => Assert.Equal(BoothTier.None,
            BoothTierRanking.HighestOf(new[] { BoothTier.None, BoothTier.None }));

    [Fact]
    public void HighestOf_picks_the_most_prestigious_across_a_mix()
        => Assert.Equal(BoothTier.Platinum,
            BoothTierRanking.HighestOf(new[] { BoothTier.Gold, BoothTier.Platinum, BoothTier.Feature }));

    [Fact]
    public void HighestOf_ignores_order()
        => Assert.Equal(BoothTier.Diamond,
            BoothTierRanking.HighestOf(new[] { BoothTier.Diamond, BoothTier.Feature, BoothTier.None }));

    [Fact]
    public void HighestOf_a_real_tier_outranks_a_blank_tier()
        => Assert.Equal(BoothTier.Feature,
            BoothTierRanking.HighestOf(new[] { BoothTier.None, BoothTier.Feature }));

    /// <summary>
    /// The raise-only rule the order-pull applies: the pull only stamps a tier
    /// when the newly-computed tier strictly OUTRANKS the stored one. This proves
    /// the comparison the pull uses (Weight(new) &gt; Weight(stored)) leaves an
    /// equal-or-lower computed tier untouched — so an organizer's manual upgrade
    /// (e.g. a comped bump from Gold to Platinum) is never silently downgraded on
    /// the next pull, while a blank/lower stored tier IS filled/raised.
    /// </summary>
    [Theory]
    [InlineData(BoothTier.None, BoothTier.Gold, true)]      // blank stored → fill
    [InlineData(BoothTier.Gold, BoothTier.Platinum, true)]  // lower stored → raise
    [InlineData(BoothTier.Gold, BoothTier.Gold, false)]     // equal → leave
    [InlineData(BoothTier.Platinum, BoothTier.Gold, false)] // higher stored (org bump) → leave
    [InlineData(BoothTier.Gold, BoothTier.None, false)]     // computed None never lowers
    public void RaiseOnly_rule_only_upgrades(BoothTier stored, BoothTier computed, bool shouldStamp)
        => Assert.Equal(shouldStamp,
            BoothTierRanking.Weight(computed) > BoothTierRanking.Weight(stored));
}
