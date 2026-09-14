using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1173 — a sponsor re-registers under a new VAT number, and the brand follows the contract.
///
/// <para>Operator 2026-09-03: <i>"it has a new vat number so completely new company … cm must keep
/// reference to the old so they are linked 1:1"</i> · <i>"as i need new contract with correct vat
/// number"</i> · <i>"i have done this before"</i>.</para>
///
/// <para>🔑 <b>The contract belongs to the legal entity; the brand does not.</b> Invoices and the ERP
/// customer stay with the entity that placed them — that is the whole point of the new company. The
/// logo and the description belong to the ORGANISATION, which has not changed.</para>
///
/// <para>NO real customer names.</para>
/// </summary>
public sealed class SponsorProfileCarryForwardTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"carry-{Guid.NewGuid():N}").Options);

    private static SponsorProfileCarryForward Svc(CommunityHubDbContext db) =>
        new(db, NullLogger<SponsorProfileCarryForward>.Instance);

    private static SponsorInfo Old() => new()
    {
        EventId = EventId,
        SponsorCompanyId = "2",
        CompanyName = "Contoso",
        CompanyDescription = "We make widgets.",
        CompanyDescriptionShort = "Widgets.",
        WebsiteUrl = "https://contoso.example",
        LinkedInUrl = "https://linkedin.example/contoso",
        LogoVectorPath = "/sp/contoso.svg",
        LogoVectorFileName = "contoso.svg",
        EventCoordinatorEmail = "coord@contoso.example",
        // The things that must NOT travel.
        ZohoSponsorId = "Z-OLD",
        ZohoExhibitorId = "Z-EX-OLD",
        Tier = Integrations.BoothTier.Gold,
        SponsorPackage = SponsorPackage.Gold,
        IsExhibitor = true,
    };

    private static SponsorInfo NewCompany() => new()
    {
        EventId = EventId,
        SponsorCompanyId = "56",
        CompanyName = "Contoso",
    };

    [Fact]
    public async Task The_brand_travels_to_the_new_company()
    {
        using var db = NewDb();
        db.SponsorInfos.AddRange(Old(), NewCompany());
        await db.SaveChangesAsync();

        var r = await Svc(db).RunAsync(EventId, "2", "56", dryRun: false);

        Assert.True(r.Ok);
        var to = db.SponsorInfos.Single(s => s.SponsorCompanyId == "56");
        Assert.Equal("We make widgets.", to.CompanyDescription);
        Assert.Equal("https://contoso.example", to.WebsiteUrl);
        Assert.Equal("contoso.svg", to.LogoVectorFileName);
        Assert.Equal("coord@contoso.example", to.EventCoordinatorEmail);
    }

    /// <summary>
    /// 🔴 The Zoho ids must NEVER travel.
    /// </summary>
    /// <remarks>
    /// Two CEH companies pointing at one Zoho record is §1159 exactly: each reconcile overwrites the
    /// other's fields, and the symptom reads as "the sync keeps reverting my edit" rather than as a
    /// linking bug.
    /// </remarks>
    [Fact]
    public async Task The_zoho_ids_never_travel()
    {
        using var db = NewDb();
        db.SponsorInfos.AddRange(Old(), NewCompany());
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId, "2", "56", dryRun: false);

        var to = db.SponsorInfos.Single(s => s.SponsorCompanyId == "56");
        Assert.Null(to.ZohoSponsorId);
        Assert.Null(to.ZohoExhibitorId);
    }

    /// <summary>
    /// 🔴 Nor does anything the ORDERS decide.
    /// </summary>
    /// <remarks>
    /// Tier, package and exhibitor status are facts about what was BOUGHT. The new company's own
    /// order settles them; copying would assert a purchase that has not happened, and the pull would
    /// then treat the sponsorship as established.
    /// </remarks>
    [Fact]
    public async Task Order_derived_state_never_travels()
    {
        using var db = NewDb();
        db.SponsorInfos.AddRange(Old(), NewCompany());
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId, "2", "56", dryRun: false);

        var to = db.SponsorInfos.Single(s => s.SponsorCompanyId == "56");
        Assert.Equal(Integrations.BoothTier.None, to.Tier);
        Assert.Equal(SponsorPackage.Silver, to.SponsorPackage);
        Assert.False(to.IsExhibitor);
    }

    /// <summary>
    /// 🔒 FILL-BLANK: a value already on the new company always wins.
    /// </summary>
    [Fact]
    public async Task An_existing_value_is_never_overwritten()
    {
        using var db = NewDb();
        var to = NewCompany();
        to.CompanyDescription = "We make better widgets now.";
        db.SponsorInfos.AddRange(Old(), to);
        await db.SaveChangesAsync();

        var r = await Svc(db).RunAsync(EventId, "2", "56", dryRun: false);

        Assert.Equal("We make better widgets now.",
            db.SponsorInfos.Single(s => s.SponsorCompanyId == "56").CompanyDescription);
        Assert.Contains(r.Skipped, s => s.StartsWith("Company description"));
    }

    [Fact]
    public async Task A_preview_writes_nothing()
    {
        using var db = NewDb();
        db.SponsorInfos.AddRange(Old(), NewCompany());
        await db.SaveChangesAsync();

        var r = await Svc(db).RunAsync(EventId, "2", "56", dryRun: true);

        Assert.NotEmpty(r.Copied);
        Assert.Null(db.SponsorInfos.Single(s => s.SponsorCompanyId == "56").CompanyDescription);
    }

    [Fact]
    public async Task The_old_company_is_left_exactly_as_it_was()
    {
        using var db = NewDb();
        db.SponsorInfos.AddRange(Old(), NewCompany());
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId, "2", "56", dryRun: false);

        var from = db.SponsorInfos.Single(s => s.SponsorCompanyId == "2");
        Assert.Equal("We make widgets.", from.CompanyDescription);
        Assert.Equal("Z-OLD", from.ZohoSponsorId);
    }

    /// <summary>
    /// 🔑 The target must EXIST — this never creates a sponsor record.
    /// </summary>
    /// <remarks>
    /// A SponsorInfo is created by the order pull when a company actually buys something. Making one
    /// here would assert a sponsorship no order supports.
    /// </remarks>
    [Fact]
    public async Task It_refuses_when_the_new_company_has_no_record_yet()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(Old());
        await db.SaveChangesAsync();

        var r = await Svc(db).RunAsync(EventId, "2", "56", dryRun: false);

        Assert.False(r.Ok);
        Assert.Contains("first completed order", r.Error);
        Assert.Single(db.SponsorInfos);
    }

    [Fact]
    public async Task It_refuses_to_copy_a_company_onto_itself()
    {
        using var db = NewDb();
        db.SponsorInfos.Add(Old());
        await db.SaveChangesAsync();

        var r = await Svc(db).RunAsync(EventId, "2", "2", dryRun: false);

        Assert.False(r.Ok);
        Assert.Contains("same company", r.Error);
    }

    [Fact]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        using var db = NewDb();
        db.SponsorInfos.AddRange(Old(), NewCompany());
        await db.SaveChangesAsync();

        await Svc(db).RunAsync(EventId, "2", "56", dryRun: false);
        var second = await Svc(db).RunAsync(EventId, "2", "56", dryRun: false);

        Assert.Empty(second.Copied);
        Assert.NotEmpty(second.Skipped);
    }
}
