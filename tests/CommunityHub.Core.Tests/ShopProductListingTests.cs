using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1312 — operator 2026-09-27: "i should have 6 available booths" · "found it under branding" · "should we link it under
/// both". A branding package that includes a feature booth (lounge sponsor with booth E-27, appreciation sponsor with E-24)
/// is listed under Branding AND Booths; organizer-only products never.
/// </summary>
public sealed class ShopProductListingTests
{
    private static ShopProduct P(ShopGroup g, string? booth) => new() { BoothId = booth, PrimaryCategory = new ShopCategory { Group = g } };

    [Fact]
    public void A_branding_package_with_a_booth_is_listed_under_both()
    {
        var lounge = P(ShopGroup.Branding, "E-27");
        Assert.True(lounge.ListedUnder(ShopGroup.Branding));
        Assert.True(lounge.ListedUnder(ShopGroup.Booths));
        Assert.False(lounge.ListedUnder(ShopGroup.Sessions));
    }

    [Fact]
    public void Without_a_booth_or_organizer_only_it_stays_in_its_own_group()
    {
        Assert.False(P(ShopGroup.Branding, null).ListedUnder(ShopGroup.Booths));
        Assert.False(P(ShopGroup.OrganizerOnly, "E-40").ListedUnder(ShopGroup.Booths));
        Assert.True(P(ShopGroup.Booths, "E-16").ListedUnder(ShopGroup.Booths));
    }
}
