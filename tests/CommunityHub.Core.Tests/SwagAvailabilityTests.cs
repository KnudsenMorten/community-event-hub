using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1165 — whether a catalogue item can be taken, joining the webshop's answer with CEH's.
///
/// <para>🔑 <b>Two systems answer half the question each.</b> The shop owns <i>sold</i> (real stock);
/// CEH owns <i>promised</i> (the organizer hold, which happens before payment and which a shop
/// cannot express). This is the one place they are joined — a second copy of the rule in a page is
/// how a sponsor ends up being offered something already promised to someone else.</para>
///
/// <para>NO real customer names.</para>
/// </summary>
public sealed class SwagAvailabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static SwagCatalogHold Hold(string companyId, DateTimeOffset? expires = null) => new()
    {
        EventId = 1,
        ProductId = 10,
        ProductName = "Branded Power Bank",
        SponsorCompanyId = companyId,
        CompanyName = "Contoso",
        ExpiresAt = expires ?? Now.AddDays(7),
    };

    [Fact]
    public void In_stock_and_unheld_is_available()
    {
        Assert.Equal(
            SwagItemState.Available,
            SwagAvailability.Resolve(inStock: true, liveHold: null, viewerCompanyId: "42"));
    }

    [Fact]
    public void A_hold_for_the_viewer_reads_as_held_for_you()
    {
        Assert.Equal(
            SwagItemState.HeldForYou,
            SwagAvailability.Resolve(true, Hold("42"), "42"));
    }

    [Fact]
    public void A_hold_for_somebody_else_is_not_offered()
    {
        Assert.Equal(
            SwagItemState.HeldForSomeoneElse,
            SwagAvailability.Resolve(true, Hold("99"), "42"));
    }

    /// <summary>
    /// 🔑 A hold keeps the item FOR them, so it must not also stop them taking it.
    /// </summary>
    [Fact]
    public void A_hold_for_you_is_still_buyable()
    {
        Assert.True(SwagAvailability.CanTake(SwagItemState.HeldForYou));
        Assert.True(SwagAvailability.CanTake(SwagItemState.Available));
        Assert.False(SwagAvailability.CanTake(SwagItemState.HeldForSomeoneElse));
        Assert.False(SwagAvailability.CanTake(SwagItemState.Taken));
    }

    /// <summary>
    /// ⚠️ SOLD BEATS HELD.
    /// </summary>
    /// <remarks>
    /// An item out of stock is gone whoever it was promised to. Telling a sponsor it is "reserved
    /// for you" when nobody can buy it is the crueller of the two wrong answers.
    /// </remarks>
    [Fact]
    public void Out_of_stock_wins_over_a_hold_even_the_viewers_own()
    {
        Assert.Equal(SwagItemState.Taken, SwagAvailability.Resolve(false, Hold("42"), "42"));
        Assert.Equal(SwagItemState.Taken, SwagAvailability.Resolve(false, null, "42"));
    }

    /// <summary>
    /// ⚠️ An organizer is nobody's company, so a hold never reads as theirs.
    /// </summary>
    /// <remarks>
    /// Without the blank check, an organizer (no company id) would match a hold whose company id was
    /// also blank — and a corrupt hold would then show as "reserved for you" to whoever opened the
    /// page.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_viewer_with_no_company_never_owns_a_hold(string? viewer)
    {
        Assert.Equal(
            SwagItemState.HeldForSomeoneElse,
            SwagAvailability.Resolve(true, Hold("42"), viewer));

        // …and not even a hold whose own company id is blank.
        Assert.Equal(
            SwagItemState.HeldForSomeoneElse,
            SwagAvailability.Resolve(true, Hold(string.Empty), viewer));
    }

    [Fact]
    public void Company_matching_ignores_case_and_padding()
    {
        Assert.Equal(SwagItemState.HeldForYou, SwagAvailability.Resolve(true, Hold(" 42 "), "42"));
    }

    /// <summary>
    /// 🔒 An EXPIRED hold is not a live hold — the caller passes only live ones.
    /// </summary>
    /// <remarks>
    /// The state machine deliberately has no "expired" input: expiry is settled by
    /// <c>SwagCatalogHoldService</c>, which releases lapsed holds, so anything reaching here is
    /// current. Two places deciding what "expired" means is how the page and the sweep would
    /// disagree.
    /// </remarks>
    [Fact]
    public void The_rule_takes_only_live_holds_so_expiry_lives_in_one_place()
    {
        var lapsed = Hold("42", expires: Now.AddDays(-1));
        Assert.False(lapsed.IsLiveAt(Now));

        // The caller filters; this asserts the contract rather than a second expiry rule here.
        Assert.Equal(
            SwagItemState.Available,
            SwagAvailability.Resolve(true, lapsed.IsLiveAt(Now) ? lapsed : null, "42"));
    }
}
