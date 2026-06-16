using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the PUBLIC sponsors read service (<see cref="PublicSponsorsService"/>,
/// REQUIREMENTS § 7). The service backs the anonymous <c>/Sponsors</c> page. Proves:
///  - sponsors are GROUPED BY TIER, highest tier first (Platinum→Diamond→Gold→Feature→Other),
///  - the LOGO FALLBACK: a raster logo path surfaces; a missing/vector logo yields
///    null (the page renders the monogram initials instead),
///  - the public NAME resolves through the shared fallback chain (CompanyName →
///    "Company {id}"),
///  - only an absolute http(s) website link is surfaced,
///  - event scoping: another edition's sponsors never leak,
///  - empty states: no active event → null; active but no sponsors → zero groups.
///
/// In-memory DbContext; synthetic ids + fake company names — no real sponsors.
/// </summary>
public sealed class PublicSponsorsServiceTests
{
    private static Event NewEvent(bool active, string code = "PUB27") => new()
    {
        Code = code, CommunityName = "Public Community",
        DisplayName = "Public Community 2027",
        StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        IsActive = active,
    };

    private static void Sponsor(
        CommunityHubDbContext db, int eventId, string companyId, BoothTier tier,
        string? companyName = null, string? logoRaster = null, string? website = null)
    {
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = eventId, SponsorCompanyId = companyId, Tier = tier,
            LogoRasterPath = logoRaster, WebsiteUrl = website,
        });
        if (companyName is not null)
        {
            db.SponsorUploadLocations.Add(new SponsorUploadLocation
            {
                EventId = eventId, SponsorCompanyId = companyId,
                CompanyName = companyName, FolderKey = "logo", Subfolder = "LOGO",
                FolderPath = $"root/{companyName}/LOGO", EditLinkUrl = "https://x.test/edit",
            });
        }
    }

    [Fact]
    public async Task No_active_event_returns_null()
    {
        using var db = TestDb.New();
        var svc = new PublicSponsorsService(db);

        Assert.Null(await svc.BuildAsync());
    }

    [Fact]
    public async Task Active_event_with_no_sponsors_returns_empty_groups()
    {
        using var db = TestDb.New();
        db.Events.Add(NewEvent(active: true));
        await db.SaveChangesAsync();

        var svc = new PublicSponsorsService(db);
        var view = await svc.BuildAsync();

        Assert.NotNull(view);
        Assert.Empty(view!.Groups);
        Assert.Equal(0, view.TotalCount);
    }

    [Fact]
    public async Task Sponsors_are_grouped_by_tier_highest_first()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        Sponsor(db, evt.Id, "1", BoothTier.Gold, "Gamma Gold");
        Sponsor(db, evt.Id, "2", BoothTier.Platinum, "Pluto Platinum");
        Sponsor(db, evt.Id, "3", BoothTier.Diamond, "Delta Diamond");
        Sponsor(db, evt.Id, "4", BoothTier.None, "Omega Other");
        await db.SaveChangesAsync();

        var svc = new PublicSponsorsService(db);
        var view = await svc.BuildAsync();

        Assert.Equal(4, view!.TotalCount);
        Assert.Equal(
            new[] { BoothTier.Platinum, BoothTier.Diamond, BoothTier.Gold, BoothTier.None },
            view.Groups.Select(g => g.Tier).ToArray());
        Assert.Equal("Platinum", view.Groups[0].DisplayName);
        Assert.Equal("Other supporters", view.Groups[3].DisplayName);
        Assert.Equal("Pluto Platinum", view.Groups[0].Sponsors.Single().Name);
    }

    [Fact]
    public async Task Multiple_sponsors_in_one_tier_are_sorted_by_name()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        Sponsor(db, evt.Id, "1", BoothTier.Gold, "Zeta Co");
        Sponsor(db, evt.Id, "2", BoothTier.Gold, "Alpha Co");
        await db.SaveChangesAsync();

        var svc = new PublicSponsorsService(db);
        var view = await svc.BuildAsync();

        var gold = Assert.Single(view!.Groups);
        Assert.Equal(new[] { "Alpha Co", "Zeta Co" }, gold.Sponsors.Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task Logo_fallback_raster_surfaces_vector_and_missing_yield_null()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        Sponsor(db, evt.Id, "1", BoothTier.Gold, "Raster Co",
            logoRaster: "uploads/sponsors/1/logo.png");
        Sponsor(db, evt.Id, "2", BoothTier.Gold, "Vector Co",
            logoRaster: "uploads/sponsors/2/logo.eps");   // not browser-renderable → null
        Sponsor(db, evt.Id, "3", BoothTier.Gold, "Nologo Co"); // no logo at all → null
        await db.SaveChangesAsync();

        var svc = new PublicSponsorsService(db);
        var view = await svc.BuildAsync();

        var byName = view!.Groups.Single().Sponsors.ToDictionary(s => s.Name);
        // Raster path is root-normalised so the page can use it directly.
        Assert.Equal("/uploads/sponsors/1/logo.png", byName["Raster Co"].LogoPath);
        // Vector + missing fall back to null → the page shows the monogram initials.
        Assert.Null(byName["Vector Co"].LogoPath);
        Assert.Null(byName["Nologo Co"].LogoPath);
        Assert.Equal("NC", byName["Nologo Co"].Initials);
    }

    [Fact]
    public async Task Name_resolves_through_fallback_chain_when_no_company_name()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        // No SponsorUploadLocation row → no captured name → "Company {id}" fallback.
        Sponsor(db, evt.Id, "9001", BoothTier.Platinum, companyName: null);
        await db.SaveChangesAsync();

        var svc = new PublicSponsorsService(db);
        var view = await svc.BuildAsync();

        Assert.Equal("Company 9001", view!.Groups.Single().Sponsors.Single().Name);
    }

    [Fact]
    public async Task Only_absolute_http_links_are_surfaced()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        Sponsor(db, evt.Id, "1", BoothTier.Gold, "Good Link Co", website: "https://good.test");
        Sponsor(db, evt.Id, "2", BoothTier.Gold, "Bad Link Co", website: "javascript:alert(1)");
        Sponsor(db, evt.Id, "3", BoothTier.Gold, "No Link Co", website: "   ");
        await db.SaveChangesAsync();

        var svc = new PublicSponsorsService(db);
        var view = await svc.BuildAsync();

        var byName = view!.Groups.Single().Sponsors.ToDictionary(s => s.Name);
        Assert.Equal("https://good.test", byName["Good Link Co"].WebsiteUrl);
        Assert.Null(byName["Bad Link Co"].WebsiteUrl);   // non-http scheme dropped
        Assert.Null(byName["No Link Co"].WebsiteUrl);    // blank dropped
    }

    [Fact]
    public async Task Sponsors_from_another_edition_never_leak()
    {
        using var db = TestDb.New();
        var active = NewEvent(active: true);
        var other = NewEvent(active: false, code: "OLD26");
        other.DisplayName = "Old 2026";
        db.Events.AddRange(active, other);
        await db.SaveChangesAsync();

        Sponsor(db, active.Id, "1", BoothTier.Gold, "This Year Co");
        Sponsor(db, other.Id, "2", BoothTier.Platinum, "Last Year Co");
        await db.SaveChangesAsync();

        var svc = new PublicSponsorsService(db);
        var view = await svc.BuildAsync();

        Assert.Equal("Public Community 2027", view!.EventDisplayName);
        Assert.Equal(1, view.TotalCount);
        Assert.Equal("This Year Co", view.Groups.Single().Sponsors.Single().Name);
    }
}
