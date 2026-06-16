using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sponsors;
using CommunityHub.Core.Participants;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the sponsor-portal read aggregate (<see cref="SponsorPortalService"/>,
/// REQUIREMENTS §20 Sponsor — "Sponsor portal"). Proves the portal is correctly
/// scoped + honestly projected:
///  - a sponsor sees ONLY their own company's leads / orders / contacts (no cross-company leak),
///  - the deliverables checklist reflects the company's sponsor tasks (pending/completed),
///  - the order/invoice status reads the ERP link entities honestly: an order with an
///    ErpOrderNumber is "in ERP", one without is pending, and ErpCustomerLinked reflects
///    whether the customer link has a number (never fabricated),
///  - the public company NAME resolves through the shared fallback chain,
///  - the logo fallback matches the public sponsors page (raster surfaces; vector/missing → null).
///
/// In-memory DbContext; synthetic ids + generic company/person names — no real sponsors.
/// </summary>
public sealed class SponsorPortalServiceTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 20, 12, 0, 0, TimeSpan.Zero);

    private static SponsorPortalService NewService(CommunityHubDbContext db) =>
        new(db, new ParticipantChecklistBuilder(db, new FixedClock(Now)));

    private static Event NewEvent(bool active = true, string code = "PORT27") => new()
    {
        Code = code, CommunityName = "Portal Community",
        DisplayName = "Portal Community 2027",
        StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        IsActive = active,
    };

    private static Participant NewSponsor(int eventId, string companyId, string name, string email) => new()
    {
        EventId = eventId, SponsorCompanyId = companyId,
        FullName = name, Email = email, Role = ParticipantRole.Sponsor, IsActive = true,
    };

    private static void Lead(
        CommunityHubDbContext db, int eventId, string companyId, string fullName,
        DateTimeOffset capturedAt, SponsorLeadStatus status = SponsorLeadStatus.Open) =>
        db.SponsorLeads.Add(new SponsorLead
        {
            EventId = eventId, SponsorCompanyId = companyId,
            FullName = fullName, Company = "Lead Co", CapturedAt = capturedAt,
            Status = status, LeadKind = SponsorLeadKind.Lead,
            CaptureMethod = SponsorLeadCaptureMethod.ManualBooth,
        });

    private static void SponsorTask(
        CommunityHubDbContext db, int eventId, string companyId, string title,
        TaskState state) =>
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId, SponsorCompanyId = companyId,
            Title = title, State = state, SourceKey = "sponsor:" + title.ToLowerInvariant(),
        });

    [Fact]
    public async Task A_sponsor_sees_only_their_own_company_data()
    {
        using var db = TestDb.New();
        var evt = NewEvent();
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var mine = NewSponsor(evt.Id, "100", "Me Booth", "me@a.test");
        db.Participants.Add(mine);
        db.Participants.Add(NewSponsor(evt.Id, "200", "Other Booth", "other@b.test"));
        await db.SaveChangesAsync();

        // My company leads/tasks/contacts + the OTHER company's, which must never leak.
        Lead(db, evt.Id, "100", "My Lead", Now.AddDays(-1));
        Lead(db, evt.Id, "200", "Their Lead", Now.AddDays(-1));
        SponsorTask(db, evt.Id, "100", "My task", TaskState.Open);
        SponsorTask(db, evt.Id, "200", "Their task", TaskState.Open);
        db.ErpOrderLinks.Add(new ErpOrderLink { EventId = evt.Id, SponsorCompanyId = "100", WebshopOrderId = 11, Currency = "DKK" });
        db.ErpOrderLinks.Add(new ErpOrderLink { EventId = evt.Id, SponsorCompanyId = "200", WebshopOrderId = 22, Currency = "DKK" });
        await db.SaveChangesAsync();

        var view = await NewService(db).BuildAsync(evt.Id, mine.Id, "100");

        Assert.Equal("100", view.CompanyId);
        Assert.Equal(1, view.LeadCount);
        Assert.Equal("My Lead", Assert.Single(view.RecentLeads).FullName);
        Assert.Single(view.Orders);
        Assert.Equal(11, view.Orders[0].WebshopOrderId);
        Assert.Equal("Me Booth", Assert.Single(view.Contacts).FullName);
        // Checklist folds in the company-scoped task — only this company's.
        Assert.Equal("My task", Assert.Single(view.Checklist.Pending).Title);
    }

    [Fact]
    public async Task Checklist_splits_pending_and_completed_sponsor_tasks()
    {
        using var db = TestDb.New();
        var evt = NewEvent();
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var me = NewSponsor(evt.Id, "100", "Me", "me@a.test");
        db.Participants.Add(me);
        await db.SaveChangesAsync();

        SponsorTask(db, evt.Id, "100", "Send logo", TaskState.Open);
        SponsorTask(db, evt.Id, "100", "Upload company info", TaskState.Done);
        await db.SaveChangesAsync();

        var view = await NewService(db).BuildAsync(evt.Id, me.Id, "100");

        Assert.Equal("Send logo", Assert.Single(view.Checklist.Pending).Title);
        Assert.Equal("Upload company info", Assert.Single(view.Checklist.Completed).Title);
        Assert.False(view.Checklist.AllComplete);
    }

    [Fact]
    public async Task Leads_hide_junk_count_visible_and_recent_are_newest_first()
    {
        using var db = TestDb.New();
        var evt = NewEvent();
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var me = NewSponsor(evt.Id, "100", "Me", "me@a.test");
        db.Participants.Add(me);
        await db.SaveChangesAsync();

        Lead(db, evt.Id, "100", "Oldest", Now.AddDays(-5));
        Lead(db, evt.Id, "100", "Newest", Now.AddDays(-1));
        Lead(db, evt.Id, "100", "Spam", Now, SponsorLeadStatus.Junk);  // hidden
        await db.SaveChangesAsync();

        var view = await NewService(db).BuildAsync(evt.Id, me.Id, "100");

        Assert.Equal(2, view.LeadCount); // junk excluded
        Assert.Equal(new[] { "Newest", "Oldest" }, view.RecentLeads.Select(l => l.FullName).ToArray());
        Assert.DoesNotContain(view.RecentLeads, l => l.FullName == "Spam");
    }

    [Fact]
    public async Task Order_status_reports_in_erp_vs_pending_honestly()
    {
        using var db = TestDb.New();
        var evt = NewEvent();
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var me = NewSponsor(evt.Id, "100", "Me", "me@a.test");
        db.Participants.Add(me);
        await db.SaveChangesAsync();

        // One order created in the ERP (has a number), one still pending (live wiring gated).
        db.ErpOrderLinks.Add(new ErpOrderLink
        {
            EventId = evt.Id, SponsorCompanyId = "100", WebshopOrderId = 1,
            ErpOrderNumber = "EC-555", Currency = "DKK", CreatedAt = Now.AddDays(-2),
        });
        db.ErpOrderLinks.Add(new ErpOrderLink
        {
            EventId = evt.Id, SponsorCompanyId = "100", WebshopOrderId = 2,
            ErpOrderNumber = "", Currency = "EUR", CreatedAt = Now.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var view = await NewService(db).BuildAsync(evt.Id, me.Id, "100");

        // Newest first.
        Assert.Equal(new long[] { 2, 1 }, view.Orders.Select(o => o.WebshopOrderId).ToArray());
        var pending = view.Orders.Single(o => o.WebshopOrderId == 2);
        var inErp = view.Orders.Single(o => o.WebshopOrderId == 1);
        Assert.False(pending.InErp);
        Assert.True(inErp.InErp);
        Assert.Equal("EC-555", inErp.ErpOrderNumber);
    }

    [Fact]
    public async Task Erp_customer_linked_only_when_customer_number_present()
    {
        using var db = TestDb.New();
        var evt = NewEvent();
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var me = NewSponsor(evt.Id, "100", "Me", "me@a.test");
        var me2 = NewSponsor(evt.Id, "200", "Me2", "me2@a.test");
        db.Participants.AddRange(me, me2);
        await db.SaveChangesAsync();

        db.ErpCustomerLinks.Add(new ErpCustomerLink { EventId = evt.Id, SponsorCompanyId = "100", ErpCustomerNumber = "C-9" });
        db.ErpCustomerLinks.Add(new ErpCustomerLink { EventId = evt.Id, SponsorCompanyId = "200", ErpCustomerNumber = "" });
        await db.SaveChangesAsync();

        var linked = await NewService(db).BuildAsync(evt.Id, me.Id, "100");
        var notLinked = await NewService(db).BuildAsync(evt.Id, me2.Id, "200");

        Assert.True(linked.ErpCustomerLinked);
        Assert.Equal("C-9", linked.ErpCustomerNumber);
        Assert.False(notLinked.ErpCustomerLinked);   // empty number → not fabricated
        Assert.Null(notLinked.ErpCustomerNumber);
    }

    [Fact]
    public async Task Name_resolves_through_fallback_chain_and_logo_fallback_matches_public_page()
    {
        using var db = TestDb.New();
        var evt = NewEvent();
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var me = NewSponsor(evt.Id, "9001", "Me", "me@a.test");
        db.Participants.Add(me);
        // No SponsorInfo, no captured name → "Company {id}" fallback; no logo → monogram.
        await db.SaveChangesAsync();

        var view = await NewService(db).BuildAsync(evt.Id, me.Id, "9001");
        Assert.Equal("Company 9001", view.CompanyName);
        Assert.Null(view.LogoPath);
        Assert.Equal("C9", view.Initials);

        // Now add a captured public name + a raster logo: both surface.
        db.SponsorUploadLocations.Add(new SponsorUploadLocation
        {
            EventId = evt.Id, SponsorCompanyId = "9001", CompanyName = "Acme Generic",
            FolderKey = "logo", Subfolder = "LOGO", FolderPath = "root/x/LOGO",
            EditLinkUrl = "https://x.test/edit",
        });
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = evt.Id, SponsorCompanyId = "9001", Tier = BoothTier.Gold,
            LogoRasterPath = "uploads/sponsors/9001/logo.png",
        });
        await db.SaveChangesAsync();

        var view2 = await NewService(db).BuildAsync(evt.Id, me.Id, "9001");
        Assert.Equal("Acme Generic", view2.CompanyName);
        Assert.Equal("/uploads/sponsors/9001/logo.png", view2.LogoPath);
        Assert.Equal(BoothTier.Gold, view2.Tier);
        Assert.Equal("Gold", view2.TierDisplay);
    }

    [Fact]
    public async Task Vector_logo_falls_back_to_monogram()
    {
        using var db = TestDb.New();
        var evt = NewEvent();
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var me = NewSponsor(evt.Id, "100", "Me", "me@a.test");
        db.Participants.Add(me);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = evt.Id, SponsorCompanyId = "100",
            LogoRasterPath = "uploads/sponsors/100/logo.eps",   // not browser-renderable
        });
        await db.SaveChangesAsync();

        var view = await NewService(db).BuildAsync(evt.Id, me.Id, "100");
        Assert.Null(view.LogoPath);
    }
}
