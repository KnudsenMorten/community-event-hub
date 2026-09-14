using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1157 — the Zoho heading a sponsor appears under comes from the PRODUCT, not the booth tier.
///
/// <para>Operator 2026-08-31: <i>"something is wrong with the category assigned for sponsors in
/// zoho, as some are linked to silver sponsor, even though the product they bought is linked to one
/// of these categories"</i> · <i>"it happens when they dont buy a exhibitor booth but non-booth
/// sponsor products"</i>.</para>
///
/// <para>🔑 <b>The category strings below are the LIVE webshop ones</b>, read off the products list
/// he sent, not invented. A needle that matches nothing is exactly how §767 shipped a sweep that
/// found no files for four production runs — the categories are the contract, so they get asserted.</para>
///
/// <para>NO real customer names.</para>
/// </summary>
public sealed class SponsorZohoCategoryMapperTests
{
    private static SponsorConfig Config() => new()
    {
        ZohoSponsorCategoryMap = new ZohoSponsorCategoryMap
        {
            Rules = new List<ZohoSponsorCategoryRule>
            {
                new() { ZohoCategory = "Session sponsors", MatchCategoryContains = new() { "Exhibitor Session Sponsors", "Exhibitor Session", "Session (" } },
                new() { ZohoCategory = "Track sponsors", MatchCategoryContains = new() { "Track Sponsor" } },
                new() { ZohoCategory = "Founding partner", MatchCategoryContains = new() { "Found Partner Sponsor" } },
                new() { ZohoCategory = "Swag sponsor", MatchCategoryContains = new() { "Swag Sponsor" } },
                new() { ZohoCategory = "Community & appreciation sponsors", MatchCategoryContains = new() { "Community & Appreciation" } },
                new() { ZohoCategory = "Gold sponsors", MatchCategoryContains = new() { "Gold Exhibitor" } },
            },
        },
    };

    private static SponsorZohoCategoryMapper New() => new(Config());

    /// <summary>
    /// 🔒 The rename he flagged: the webshop category "Exhibitor Session" became
    /// "Exhibitor Session Sponsors".
    /// </summary>
    [Fact]
    public void The_renamed_session_category_still_maps()
    {
        Assert.Equal(
            new[] { "Session sponsors" },
            New().MapProduct("Exhibitor Session Sponsors, Session (20 min)"));
    }

    [Fact]
    public void Track_sponsor_products_are_track_sponsors_not_the_booth_tier()
    {
        // A pre-day master class carries the booth umbrella AND Track Sponsors. The umbrella is not
        // a heading, so the only answer is Track sponsors — this is the case that used to come out
        // as the tier, because the tier was the only thing consulted.
        Assert.Equal(
            new[] { "Track sponsors" },
            New().MapProduct("Exhibitor Tier Package with Booth, Track Sponsors"));
    }

    /// <summary>
    /// 🔑 ONE product, TWO headings — the shape that makes the whole feature necessary.
    /// </summary>
    [Fact]
    public void A_product_in_two_categories_yields_two_headings()
    {
        var mapped = New().MapProduct("Community & Appreciation Sponsor, Found Partner Sponsor");

        Assert.Equal(2, mapped.Count);
        Assert.Contains("Founding partner", mapped);
        Assert.Contains("Community & appreciation sponsors", mapped);
    }

    /// <summary>
    /// 🔑 Three products in three categories give three headings — his sentence, as a test.
    /// </summary>
    [Fact]
    public void Three_products_in_three_categories_give_three_headings()
    {
        var mapped = New().MapCompany(new[]
        {
            "Swag Sponsor",
            "Exhibitor Session Sponsors, Session (60 min)",
            "Exhibitor Tier Package with Booth, Gold Exhibitor (Shared)",
        });

        Assert.Equal(new[] { "Gold sponsors", "Session sponsors", "Swag sponsor" }, mapped);
    }

    [Fact]
    public void Matching_is_case_insensitive_and_blank_is_nothing()
    {
        Assert.Equal(new[] { "Swag sponsor" }, New().MapProduct("swag sponsor"));
        Assert.Empty(New().MapProduct(null));
        Assert.Empty(New().MapProduct("   "));
    }

    /// <summary>
    /// ⚠️ An unmapped product maps to NOTHING rather than to a guess.
    /// </summary>
    /// <remarks>
    /// The caller keeps its pre-§1157 behaviour for such a company (one record, package-derived).
    /// Inventing a heading Zoho has no pinned id for would simply fail the create, so a wrong guess
    /// would cost a real sponsor their listing rather than merely misfiling it.
    /// </remarks>
    [Fact]
    public void An_unmapped_product_maps_to_nothing()
    {
        Assert.Empty(New().MapProduct("Exhibitor Tier Package with Booth, Feature Exhibitor"));
        Assert.Empty(New().MapProduct("Booth Furniture"));
    }

    [Fact]
    public void An_edition_with_no_map_is_empty_so_the_caller_can_fall_back()
    {
        var mapper = new SponsorZohoCategoryMapper(new SponsorConfig());
        Assert.True(mapper.IsEmpty);
        Assert.Empty(mapper.MapProduct("Swag Sponsor"));
    }

    // --- entitlement storage -------------------------------------------------------------

    private static SponsorInfo Company() => new() { SponsorCompanyId = "42", CompanyName = "Contoso" };

    [Fact]
    public void Categories_round_trip_and_merge_is_a_union()
    {
        var info = Company();
        Assert.True(SponsorZohoLinks.MergeCategories(info, new[] { "Swag sponsor" }));
        Assert.True(SponsorZohoLinks.MergeCategories(info, new[] { "Session sponsors" }));

        Assert.Equal(new[] { "Session sponsors", "Swag sponsor" }, SponsorZohoLinks.ReadCategories(info));
    }

    [Fact]
    public void Merging_what_is_already_there_reports_no_change()
    {
        var info = Company();
        SponsorZohoLinks.MergeCategories(info, new[] { "Swag sponsor" });

        // 🔒 The return value drives `changed` in the order pull; a false positive would mark every
        // sponsor row dirty on every 15-minute run.
        Assert.False(SponsorZohoLinks.MergeCategories(info, new[] { "swag SPONSOR" }));
    }

    /// <summary>
    /// 🔒 RAISE-ONLY: a category is never taken away by a later pull.
    /// </summary>
    /// <remarks>
    /// CEH has no delete path into Zoho (§56), so dropping the entitlement would leave a live record
    /// that nothing owns — invisible to the sync rather than removed from Zoho. The orphan is
    /// reported for a human to decide on instead.
    /// </remarks>
    [Fact]
    public void A_category_is_never_removed_by_a_later_pull()
    {
        var info = Company();
        SponsorZohoLinks.MergeCategories(info, new[] { "Swag sponsor", "Session sponsors" });
        SponsorZohoLinks.MergeCategories(info, new[] { "Swag sponsor" });   // the session is gone

        Assert.Contains("Session sponsors", SponsorZohoLinks.ReadCategories(info));
    }

    [Fact]
    public void Unreadable_category_json_is_empty_not_an_exception()
    {
        var info = Company();
        info.ZohoSponsorCategoriesJson = "{ not json";
        Assert.Empty(SponsorZohoLinks.ReadCategories(info));
    }

    /// <summary>
    /// 🔒 The primary id does NOT move when a category is added.
    /// </summary>
    /// <remarks>
    /// The profile-pushed hash, the stale-id self-heal and every page showing "the" Zoho id read the
    /// primary. If adding a heading re-pointed it at a different record, all of them would quietly
    /// start describing a different record than the one they described yesterday.
    /// </remarks>
    [Fact]
    public void Adding_a_category_does_not_move_the_primary_id()
    {
        var info = Company();
        SponsorZohoLinks.Write(info, new[] { new SponsorZohoLink("Swag sponsor", "C-1", "Z-1") });
        Assert.Equal("Z-1", info.ZohoSponsorId);

        // "Attendee …" sorts BEFORE "Swag …", so a first-wins primary would move here.
        SponsorZohoLinks.Write(info, new[]
        {
            new SponsorZohoLink("Swag sponsor", "C-1", "Z-1"),
            new SponsorZohoLink("Attendee experience sponsors", "C-2", "Z-2"),
        });

        Assert.Equal("Z-1", info.ZohoSponsorId);
    }

    [Fact]
    public void The_primary_moves_only_when_the_record_it_points_at_is_gone()
    {
        var info = Company();
        SponsorZohoLinks.Write(info, new[] { new SponsorZohoLink("Swag sponsor", "C-1", "Z-1") });
        SponsorZohoLinks.Write(info, new[] { new SponsorZohoLink("Track sponsors", "C-2", "Z-2") });

        Assert.Equal("Z-2", info.ZohoSponsorId);
    }
}
