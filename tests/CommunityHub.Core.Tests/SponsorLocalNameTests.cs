using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1312 — operator 2026-09-29: a shop-registered company (<c>ceh-…</c>, not in Company Manager) uploaded its logos and
/// they were named <c>company-ceh-14ca083d-logo-web-1.png</c> and mailed as "Company ceh-14ca083d". The sponsor-page name
/// resolvers now fall back to the name CEH itself holds.
/// </summary>
public class SponsorLocalNameTests
{
    [Fact]
    public async Task The_sponsor_record_name_wins_then_the_shop_company_name()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        db.ShopCompanies.Add(new ShopCompany { EventId = ev.Id, SponsorCompanyId = "ceh-shoponly", LegalName = "Shop Only SIA", PublicName = "Shop Only" });
        db.ShopCompanies.Add(new ShopCompany { EventId = ev.Id, SponsorCompanyId = "ceh-both", LegalName = "Legal Both SIA" });
        db.SponsorInfos.Add(new SponsorInfo { EventId = ev.Id, SponsorCompanyId = "ceh-both", CompanyName = "Master Packager SIA" });
        await db.SaveChangesAsync();

        Assert.Equal("Master Packager SIA", await SponsorCompanyNameService.LocalNameAsync(db, "ceh-both"));
        Assert.Equal("Shop Only", await SponsorCompanyNameService.LocalNameAsync(db, "ceh-shoponly"));
        Assert.Null(await SponsorCompanyNameService.LocalNameAsync(db, "ceh-unknown"));

        // …and the upload file name that follows from it.
        Assert.Equal("master-packager-sia-logo-web-1.png",
            CommunityHub.Uploads.SponsorUploadNaming.Build("some", "Master Packager SIA", 1, ".png"));
    }

    /// <summary>
    /// 🔴 §1312 — operator 2026-09-29 (confirmed): the ERP → CEH sync CREATES the public name — the ERP name without its
    /// legal form — only while it is empty; once filled (by the rule or by the operator) it is never changed again.
    /// </summary>
    [Fact]
    public void The_erp_sync_fills_an_empty_public_name_once_and_never_overwrites_one()
    {
        var fresh = new ShopCompany { LegalName = "old" };
        Assert.True(ShopCompanyService.ApplyErpBilling(fresh, "Acme ApS", null, null, null, null, null, null, null, null));
        Assert.Equal("Acme", fresh.PublicName);
        Assert.Equal("Acme ApS", fresh.LegalName);

        // The operator's value — which the rule would never produce — survives every later sync, even a legal-name change.
        var set = new ShopCompany { LegalName = "Admin By Request Nordics ApS", PublicName = "Admin By Request" };
        ShopCompanyService.ApplyErpBilling(set, "Admin By Request Nordics ApS", null, null, null, null, null, null, null, null);
        ShopCompanyService.ApplyErpBilling(set, "Admin By Request Nordic A/S", null, null, null, null, null, null, null, null);
        Assert.Equal("Admin By Request", set.PublicName);
        Assert.Equal("Admin By Request Nordic A/S", set.LegalName);   // the legal name still follows e-conomic
    }

    /// <summary>
    /// §1312 audit (2026-09-29) — "public name is for SoMe, Zoho, etc everything public": a public name is NEVER the legal
    /// name with its legal form; the shared resolver reads the CEH shop; the shop holds the e-conomic number.
    /// </summary>
    [Fact]
    public async Task Public_names_come_from_CEH_and_never_carry_a_legal_form()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        db.ShopCompanies.Add(new ShopCompany { EventId = ev.Id, SponsorCompanyId = "ceh-legal", LegalName = "Acme ApS", ErpCustomerNumber = "341409360" });
        db.ShopCompanies.Add(new ShopCompany { EventId = ev.Id, SponsorCompanyId = "ceh-pub", LegalName = "Beta SIA", PublicName = "Beta" });
        db.ErpCustomerLinks.Add(new ErpCustomerLink { EventId = ev.Id, SponsorCompanyId = "erp-only", CompanyName = "Gamma GmbH" });
        await db.SaveChangesAsync();

        // No public name ⇒ the legal name without its form, never "Acme ApS".
        Assert.Equal("Acme", await SponsorCompanyNameService.LocalNameAsync(db, "ceh-legal"));
        Assert.Equal("Acme", (await db.ShopCompanies.SingleAsync(c => c.SponsorCompanyId == "ceh-legal")).PublicLabel);

        // The shared resolver (uploads, SoMe queue, AI helper, Zoho) reads the CEH shop — no upload-folder row needed —
        // and its last resort, the ERP (legal) name, loses its legal form too.
        var names = await SponsorCompanyNameService.ResolveFromLocalAsync(db, ev.Id, new[] { "ceh-legal", "ceh-pub", "erp-only" });
        Assert.Equal("Acme", names["ceh-legal"]);
        Assert.Equal("Beta", names["ceh-pub"]);
        Assert.Equal("Gamma", names["erp-only"]);

        // The e-conomic customer number the contact pages use, from the CEH shop (a ceh- company has no CM record).
        Assert.Equal(341409360, await ShopCompanyErp.CustomerNumberAsync(db, "ceh-legal"));
        Assert.Null(await ShopCompanyErp.CustomerNumberAsync(db, "ceh-pub"));
    }
}
