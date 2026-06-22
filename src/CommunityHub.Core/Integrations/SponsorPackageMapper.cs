using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Maps a purchased WooCommerce product / tier NAME to the commercial
/// <see cref="SponsorPackage"/>. Case-insensitive "contains" match, richest
/// package first so e.g. a name carrying both "gold" and "platinum" resolves to
/// the higher one. Anything that matches nothing falls back to
/// <see cref="SponsorPackage.Silver"/> (the digital / no-booth default).
/// </summary>
public static class SponsorPackageMapper
{
    /// <summary>
    /// Resolve a <see cref="SponsorPackage"/> from a product / tier name.
    /// Returns <see cref="SponsorPackage.Silver"/> for null/blank/unmatched.
    /// </summary>
    public static SponsorPackage FromProductName(string? productName)
    {
        var name = (productName ?? string.Empty).ToLowerInvariant();
        if (name.Contains("platinum")) return SponsorPackage.Platinum;
        if (name.Contains("diamond")) return SponsorPackage.Diamond;
        if (name.Contains("gold")) return SponsorPackage.Gold;
        if (name.Contains("silver")) return SponsorPackage.Silver;
        return SponsorPackage.Silver;
    }

    /// <summary>
    /// Map the already-classified booth <see cref="BoothTier"/> to a
    /// <see cref="SponsorPackage"/>. Used by the sponsor-order pull, which has
    /// already resolved the company's highest tier from its line items. Silver is
    /// the digital default for a company with no booth tier.
    /// </summary>
    public static SponsorPackage FromBoothTier(BoothTier tier) => tier switch
    {
        BoothTier.Platinum => SponsorPackage.Platinum,
        BoothTier.Diamond => SponsorPackage.Diamond,
        // Gold and the bundled "Feature" booth packs are booth/exhibitor levels.
        BoothTier.Gold => SponsorPackage.Gold,
        BoothTier.Feature => SponsorPackage.Gold,
        _ => SponsorPackage.Silver,
    };
}
