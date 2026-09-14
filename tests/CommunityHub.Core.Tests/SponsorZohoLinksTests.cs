using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1157 — a company can hold SEVERAL Zoho sponsor records, one per sponsorship category.
///
/// <para>Operator 2026-08-31: <i>"a sponsor that buys 3 products that fits into 3 categories must be
/// created 3 times and linked to each category"</i> · <i>"it is important, that ceh stored multiple
/// ids in the sponsor field (array), so any updates happens to all entries … like company name,
/// company description, company website"</i>.</para>
///
/// <para>🔑 Confirmed against last year's event, where ARROW appears under both COMMUNITY &amp;
/// APPRECIATION and CONTENT &amp; PROGRAM with the same contact — the established shape in Zoho,
/// not a new idea.</para>
///
/// <para>NO real customer names.</para>
/// </summary>
public class SponsorZohoLinksTests
{
    private static SponsorInfo New(string? primary = null, string? json = null) =>
        new() { SponsorCompanyId = "42", CompanyName = "Contoso", ZohoSponsorId = primary, ZohoSponsorLinksJson = json };

    [Fact]
    public void Links_round_trip()
    {
        var info = New();
        SponsorZohoLinks.Write(info, new[]
        {
            new SponsorZohoLink("Swag sponsor", "C-1", "Z-1"),
            new SponsorZohoLink("Content & program sponsors", "C-2", "Z-2"),
        });

        var read = SponsorZohoLinks.Read(info);
        Assert.Equal(2, read.Count);
        Assert.Contains(read, l => l.ZohoSponsorId == "Z-1" && l.CategoryName == "Swag sponsor");
        Assert.Contains(read, l => l.ZohoSponsorId == "Z-2");
    }

    /// <summary>
    /// 🔑 The answer to "any updates happens to all entries".
    /// </summary>
    [Fact]
    public void AllSponsorIds_returns_every_record_to_keep_in_step()
    {
        var info = New();
        SponsorZohoLinks.Write(info, new[]
        {
            new SponsorZohoLink("Swag sponsor", "C-1", "Z-1"),
            new SponsorZohoLink("Community & appreciation sponsors", "C-2", "Z-2"),
            new SponsorZohoLink("Content & program sponsors", "C-3", "Z-3"),
        });

        Assert.Equal(new[] { "Z-1", "Z-2", "Z-3" }, SponsorZohoLinks.AllSponsorIds(info).OrderBy(x => x));
    }

    /// <summary>
    /// 🔒 The primary id keeps working, so every pre-§1157 caller is untouched.
    /// </summary>
    [Fact]
    public void Writing_links_keeps_the_primary_id_pointing_at_one_of_them()
    {
        var info = New();
        SponsorZohoLinks.Write(info, new[]
        {
            new SponsorZohoLink("Swag sponsor", "C-1", "Z-1"),
            new SponsorZohoLink("Attendee experience sponsors", "C-2", "Z-2"),
        });

        Assert.False(string.IsNullOrWhiteSpace(info.ZohoSponsorId));
        Assert.Contains(info.ZohoSponsorId!, new[] { "Z-1", "Z-2" });
    }

    /// <summary>
    /// ⚠️ A company provisioned BEFORE §1157 has a primary id and no recorded category.
    /// </summary>
    /// <remarks>
    /// It must still be updated — the record is real — so it surfaces as a link with an empty
    /// category rather than being dropped. Dropping it would stop name/website/description reaching
    /// a live Zoho record, which is a silent regression for every existing sponsor.
    /// </remarks>
    [Fact]
    public void A_legacy_company_with_only_the_primary_id_is_still_a_link()
    {
        var info = New(primary: "Z-OLD");

        var read = SponsorZohoLinks.Read(info);
        Assert.Single(read);
        Assert.Equal("Z-OLD", read[0].ZohoSponsorId);
        Assert.Equal(string.Empty, read[0].CategoryName);
        Assert.Equal(new[] { "Z-OLD" }, SponsorZohoLinks.AllSponsorIds(info));
    }

    [Fact]
    public void A_company_with_nothing_has_no_links()
    {
        Assert.Empty(SponsorZohoLinks.Read(New()));
        Assert.Empty(SponsorZohoLinks.AllSponsorIds(New()));
    }

    /// <summary>
    /// 🔒 Unreadable JSON returns EMPTY and never throws.
    /// </summary>
    /// <remarks>
    /// The sync runs unattended every ten minutes; an exception here would take the whole reconcile
    /// down for every company because one row's column was malformed.
    /// </remarks>
    [Fact]
    public void Malformed_json_is_empty_not_an_exception()
    {
        var info = New(primary: "Z-1", json: "{ this is not json");
        Assert.Empty(SponsorZohoLinks.Read(info));
    }

    [Fact]
    public void The_same_id_is_never_listed_twice()
    {
        // A duplicate would make the fan-out PUT to the same record twice on every run.
        var info = New();
        SponsorZohoLinks.Write(info, new[]
        {
            new SponsorZohoLink("Swag sponsor", "C-1", "Z-1"),
            new SponsorZohoLink("Swag sponsor", "C-1", "Z-1"),
        });

        Assert.Single(SponsorZohoLinks.Read(info));
    }

    [Fact]
    public void HasCategory_answers_whether_a_record_already_exists()
    {
        // 🔑 This is what stops the provisioner creating a SECOND record under a category the
        // company already has — the failure that would put a company in a list twice.
        var info = New();
        SponsorZohoLinks.Write(info, new[] { new SponsorZohoLink("Swag sponsor", "C-1", "Z-1") });

        Assert.True(SponsorZohoLinks.HasCategory(info, "Swag sponsor"));
        Assert.True(SponsorZohoLinks.HasCategory(info, "  swag SPONSOR "));
        Assert.False(SponsorZohoLinks.HasCategory(info, "Content & program sponsors"));
    }

    [Fact]
    public void Writing_an_empty_set_clears_the_column()
    {
        var info = New(primary: "Z-1", json: "[]");
        SponsorZohoLinks.Write(info, Array.Empty<SponsorZohoLink>());

        Assert.Null(info.ZohoSponsorLinksJson);
        // 🔒 The primary is NOT cleared: a null primary would make the company look unprovisioned
        // and invite a duplicate create in Zoho.
        Assert.Equal("Z-1", info.ZohoSponsorId);
    }
}
