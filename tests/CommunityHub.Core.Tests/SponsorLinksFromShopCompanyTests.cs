using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1360 — the public name, website, LinkedIn and X are ONE field each, on the company page. Operator 2026-10-19 (the weekly
/// "missing a webshop website or LinkedIn" mail for Egiss, whose links the company page had): *"why do we have double ?"* →
/// *"one field, truesec.dk"* · *"dont forget the public name"* · *"legal name goes on contracts, invoices"* · *"the public name
/// today stays as today, as that is decided by the customers"* · *"we set the public name as one-time if blank"*.
/// </summary>
public sealed class SponsorLinksFromShopCompanyTests
{
    private static async Task<(CommunityHubDbContext Db, int EventId)> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        db.SponsorInfos.AddRange(
            // Egiss: the sponsor record (the copy) is blank; the company page has both links.
            new SponsorInfo { EventId = ev.Id, SponsorCompanyId = "106", CompanyName = "Egiss" },
            // Truesec: the copy says .dk; the company page (now corrected to .dk) is the one that counts.
            new SponsorInfo { EventId = ev.Id, SponsorCompanyId = "108", CompanyName = "Truesec", WebsiteUrl = "https://www.truesec.com" },
            // Carried forward: the copy has an X link the company page does not — the page takes it once.
            new SponsorInfo { EventId = ev.Id, SponsorCompanyId = "19", CompanyName = "Patch My PC", TwitterUrl = "https://x.com/patchmypc" });
        db.ShopCompanies.AddRange(
            new ShopCompany { EventId = ev.Id, SponsorCompanyId = "106", PublicName = "Egiss", LegalName = "Egiss A/S",
                              Website = "https://egiss.net", LinkedInUrl = "https://www.linkedin.com/company/egiss" },
            new ShopCompany { EventId = ev.Id, SponsorCompanyId = "108", PublicName = "Truesec", LegalName = "Truesec A/S",
                              Website = "https://www.truesec.dk" },
            new ShopCompany { EventId = ev.Id, SponsorCompanyId = "19", PublicName = "Patch My PC", LegalName = "Patch My PC LLC" });
        await db.SaveChangesAsync();
        return (db, ev.Id);
    }

    [Fact]
    public async Task Every_reader_gets_the_company_page_value_not_the_sponsor_record_copy()
    {
        var (db, eventId) = await SeedAsync();

        var egiss = await CompanyLinks.ForAsync(db, eventId, "106");
        Assert.Equal("https://egiss.net", egiss.Website);
        Assert.Equal("https://www.linkedin.com/company/egiss", egiss.LinkedIn);
        Assert.Equal("https://www.truesec.dk", (await CompanyLinks.AllAsync(db, eventId))["108"].Website);
    }

    [Fact]
    public async Task A_company_without_a_company_page_falls_back_to_the_sponsor_record()
    {
        var (db, eventId) = await SeedAsync();
        db.SponsorInfos.Add(new SponsorInfo { EventId = eventId, SponsorCompanyId = "999", CompanyName = "Old Co", WebsiteUrl = "https://old.example" });
        await db.SaveChangesAsync();

        Assert.Equal("https://old.example", (await CompanyLinks.ForAsync(db, eventId, "999")).Website);
    }

    [Fact]
    public async Task The_sync_copies_the_page_down_and_a_blank_page_field_takes_the_copy_once()
    {
        var (db, eventId) = await SeedAsync();

        var result = await ShopCompanyLinks.SyncFromCompanyPageAsync(db, eventId);

        var infos = await db.SponsorInfos.IgnoreQueryFilters().ToDictionaryAsync(i => i.SponsorCompanyId);
        Assert.Equal("https://egiss.net", infos["106"].WebsiteUrl);                          // copy filled from the page
        Assert.Equal("https://www.linkedin.com/company/egiss", infos["106"].LinkedInUrl);
        Assert.Equal("https://www.truesec.dk", infos["108"].WebsiteUrl);                     // the page wins a difference
        var pmpPage = await db.ShopCompanies.SingleAsync(c => c.SponsorCompanyId == "19");
        Assert.Equal("https://x.com/patchmypc", pmpPage.TwitterUrl);                         // blank page took the copy once
        Assert.Equal(1, result.PageFieldsAbsorbed);

        Assert.Equal(new ShopCompanyLinks.SyncResult(0, 0), await ShopCompanyLinks.SyncFromCompanyPageAsync(db, eventId));   // settled
    }

    [Fact]
    public async Task The_public_name_stays_as_the_customer_set_it_and_the_legal_name_is_never_touched()
    {
        var (db, eventId) = await SeedAsync();
        var egissPage = await db.ShopCompanies.SingleAsync(c => c.SponsorCompanyId == "106");
        egissPage.PublicName = "EGISS";   // the customer changed it (their form writes the company page)
        await db.SaveChangesAsync();

        await ShopCompanyLinks.SyncFromCompanyPageAsync(db, eventId);

        Assert.Equal("EGISS", (await db.SponsorInfos.IgnoreQueryFilters().SingleAsync(i => i.SponsorCompanyId == "106")).CompanyName);
        Assert.Equal("Egiss A/S", (await db.ShopCompanies.SingleAsync(c => c.SponsorCompanyId == "106")).LegalName);
        Assert.Equal("EGISS", await SponsorCompanyNameService.LocalNameAsync(db, "106"));   // name lookup reads the page first
    }

    [Fact]
    public async Task A_blank_public_name_on_the_page_is_set_once_from_the_copy()
    {
        var (db, eventId) = await SeedAsync();
        var page = await db.ShopCompanies.SingleAsync(c => c.SponsorCompanyId == "19");
        page.PublicName = null;
        await db.SaveChangesAsync();

        await ShopCompanyLinks.SyncFromCompanyPageAsync(db, eventId);

        Assert.Equal("Patch My PC", (await db.ShopCompanies.SingleAsync(c => c.SponsorCompanyId == "19")).PublicName);
    }
}
