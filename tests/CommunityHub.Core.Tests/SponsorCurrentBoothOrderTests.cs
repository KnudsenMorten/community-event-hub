using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1163 — a cancelled booth order must not be re-created in Zoho on the next pull.
///
/// <para>Operator 2026-08-31: <i>"i have cancelled the order"</i> · <i>"but the sponsor still have 1
/// order - but it is not exhibitor anymore"</i> · <i>"so i bet it is isexhibitor = 1 which must be
/// 0"</i> — after deleting the company's exhibitor record by hand and being told, by mail, that CEH
/// had tried to create it again.</para>
///
/// <para>🔴 <b>He was right about the mechanism.</b> <see cref="SponsorInfo.IsExhibitor"/> is
/// raise-only, so <see cref="SponsorInfo.HasBooth"/> stays true after a cancellation and the
/// provisioner saw a booth company with no exhibitor record. The fix is NOT to clear the raise-only
/// flag — that exists so an organizer's manual correction survives a re-pull — but to gate the
/// CREATE on the live order truth.</para>
///
/// <para>NO real customer names.</para>
/// </summary>
public sealed class SponsorCurrentBoothOrderTests
{
    private static SponsorInfo Company() => new()
    {
        SponsorCompanyId = "42",
        CompanyName = "Contoso",
        // The state after a cancellation: raise-only fields still say "booth".
        IsExhibitor = true,
        Tier = BoothTier.Gold,
        SponsorPackage = SponsorPackage.Gold,
    };

    /// <summary>
    /// 🔴 The exact production state: raise-only says booth, the live orders say otherwise.
    /// </summary>
    [Fact]
    public void The_raise_only_flags_still_say_booth_after_a_cancellation()
    {
        var info = Company();
        info.HasCurrentBoothOrder = false;

        // 🔑 This is the whole point: HasBooth cannot answer "may we create?", because it is true
        // for a company whose booth order was cancelled ten minutes ago.
        Assert.True(info.HasBooth);
        Assert.False(info.HasCurrentBoothOrder);
    }

    /// <summary>
    /// 🔒 The new field defaults to FALSE, so an unknown state creates nothing.
    /// </summary>
    /// <remarks>
    /// The column lands empty for every existing company until the first pull writes it. Defaulting
    /// to true would have meant one pull's worth of re-created exhibitor records — the failure this
    /// exists to stop — so the default is the safe direction and costs only a few minutes' delay.
    /// </remarks>
    [Fact]
    public void An_unwritten_flag_is_false_so_nothing_is_created()
    {
        Assert.False(new SponsorInfo().HasCurrentBoothOrder);
    }

    [Fact]
    public void A_company_that_still_has_its_booth_order_is_unaffected()
    {
        var info = Company();
        info.HasCurrentBoothOrder = true;

        Assert.True(info.HasBooth);
        Assert.True(info.HasCurrentBoothOrder);
    }

    /// <summary>
    /// ⚠️ The flag is about the BOOTH, not about being a sponsor at all.
    /// </summary>
    /// <remarks>
    /// Operator: <i>"it used to be exhibitor, now it is only sponsor"</i>. The company keeps its
    /// sponsor record and its remaining category; only the exhibitor side is gone. A flag that
    /// conflated the two would have removed them from the sponsor listing as well.
    /// </remarks>
    [Fact]
    public void Losing_the_booth_does_not_make_them_a_non_sponsor()
    {
        var info = Company();
        info.IsSponsor = true;
        info.HasCurrentBoothOrder = false;
        SponsorZohoLinks.MergeCategories(info, new[] { "Founding partner" });

        Assert.True(info.IsSponsor);
        Assert.Equal(new[] { "Founding partner" }, SponsorZohoLinks.ReadCategories(info));
    }

    /// <summary>
    /// 🔒 Clearing the booth order does NOT clear the recorded Zoho exhibitor id.
    /// </summary>
    /// <remarks>
    /// The id is CEH's record of something that exists (or existed) in Zoho. Blanking it would make
    /// the company look unprovisioned and invite a duplicate create — the opposite of the fix — and
    /// it would also lose the id §1161 needs in order to tell him WHICH record to remove.
    /// </remarks>
    [Fact]
    public void The_recorded_exhibitor_id_survives_the_cancellation()
    {
        var info = Company();
        info.ZohoExhibitorId = "Z-EX-1";
        info.HasCurrentBoothOrder = false;

        Assert.Equal("Z-EX-1", info.ZohoExhibitorId);
    }
}
