namespace CommunityHub.Core.Integrations;

/// <summary>
/// The single, pure authority for RANKING booth tiers, so "which tier is higher"
/// is decided in one place and the order can never drift between the order-pull
/// (which STAMPS the company's highest tier onto <see cref="Domain.SponsorInfo.Tier"/>)
/// and the public sponsors page (which GROUPS by tier, highest first).
///
/// Rank order (most prestigious first): Platinum &gt; Diamond &gt; Gold &gt; Feature
/// &gt; None. <see cref="BoothTier.None"/> is the lowest — "no/unknown tier" — so a
/// company that orders any real booth always outranks a blank tier.
/// </summary>
public static class BoothTierRanking
{
    /// <summary>
    /// A bigger number = a more prestigious tier. Mirrors the display order on the
    /// public sponsors page (Platinum first). <see cref="BoothTier.None"/> is 0.
    /// </summary>
    public static int Weight(BoothTier tier) => tier switch
    {
        BoothTier.Platinum => 4,
        BoothTier.Diamond => 3,
        BoothTier.Gold => 2,
        BoothTier.Feature => 1,
        _ => 0, // None / unknown
    };

    /// <summary>
    /// The highest (most prestigious) tier across a set of booth tiers, e.g. when a
    /// sponsor places several booth orders. Returns <see cref="BoothTier.None"/> for
    /// an empty set or a set that is all <see cref="BoothTier.None"/>.
    /// </summary>
    public static BoothTier HighestOf(IEnumerable<BoothTier> tiers)
    {
        var best = BoothTier.None;
        foreach (var t in tiers)
        {
            if (Weight(t) > Weight(best)) best = t;
        }
        return best;
    }
}
