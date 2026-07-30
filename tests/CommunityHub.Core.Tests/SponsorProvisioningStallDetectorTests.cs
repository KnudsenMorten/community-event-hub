using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §335 — a Gold+ booth company whose SharePoint upload folder never gets provisioned is
/// blocked from its welcome e-mail FOREVER, and the only symptom is an absence: the pull's
/// catch blocks log a warning and carry on. These tests pin the behaviour that turns that
/// silence into an Action-queue item — including the two ways it must NOT be noisy (a company
/// still plausibly in flight, and a company that recovered).
/// </summary>
public class SponsorProvisioningStallDetectorTests
{
    private const int EventId = 7;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-26T10:00:00Z");

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"stall-{Guid.NewGuid():N}")
            .Options);

    private static SponsorProvisioningStallDetector Sut(CommunityHubDbContext db)
    {
        var clock = new FixedClock();
        return new SponsorProvisioningStallDetector(
            db, clock, new OrganizerActionItemService(db, clock));
    }

    private static async Task SeedEventAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9),
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedCompanyAsync(
        CommunityHubDbContext db, string companyId, SponsorPackage package, int ageDays)
    {
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = companyId,
            SponsorPackage = package, CreatedAt = Now.AddDays(-ageDays),
        });
        await db.SaveChangesAsync();
    }

    private static async Task ProvisionAsync(CommunityHubDbContext db, string companyId)
    {
        db.SponsorUploadLocations.Add(new SponsorUploadLocation
        {
            EventId = EventId, SponsorCompanyId = companyId, CompanyName = "Acme",
            FolderKey = "logos", EditLinkUrl = "https://example.test/edit",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Raises_an_item_for_a_booth_company_stuck_past_the_threshold()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedCompanyAsync(db, "42", SponsorPackage.Gold, ageDays: 3);

        var result = await Sut(db).RunAsync(EventId);

        Assert.Equal(1, result.Stuck);
        var item = await db.OrganizerActionItems.SingleAsync();
        Assert.Null(item.ResolvedAt);
        Assert.Equal($"{SponsorProvisioningStallDetector.TypePrefix}:42", item.Type);
        Assert.Contains("3 day(s)", item.Summary);
        // The queue label must recognise the per-company type, not fall back to the raw code.
        Assert.Equal("Sponsor upload folder not provisioned",
            OrganizerActionItemService.LabelFor(item.Type));
    }

    /// <summary>The pull runs every ~30 min, so a brand-new company is in flight, not stuck.</summary>
    [Fact]
    public async Task Stays_quiet_for_a_company_that_is_still_plausibly_in_flight()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedCompanyAsync(db, "42", SponsorPackage.Gold, ageDays: 0);

        var result = await Sut(db).RunAsync(EventId);

        Assert.Equal(0, result.Stuck);
        Assert.Empty(await db.OrganizerActionItems.ToListAsync());
    }

    /// <summary>Digital/Silver companies have nothing to provision and are never gated.</summary>
    [Fact]
    public async Task Ignores_a_company_below_the_booth_threshold()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedCompanyAsync(db, "42", SponsorPackage.Silver, ageDays: 30);

        var result = await Sut(db).RunAsync(EventId);

        Assert.Equal(0, result.Stuck);
        Assert.Empty(await db.OrganizerActionItems.ToListAsync());
    }

    [Fact]
    public async Task Stays_quiet_for_a_company_that_is_already_provisioned()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedCompanyAsync(db, "42", SponsorPackage.Gold, ageDays: 30);
        await ProvisionAsync(db, "42");

        var result = await Sut(db).RunAsync(EventId);

        Assert.Equal(0, result.Stuck);
        Assert.Empty(await db.OrganizerActionItems.ToListAsync());
    }

    /// <summary>
    /// SELF-HEALING: a company that recovers must not leave a stale "stuck" row behind — an
    /// action queue that lies about the current state is worse than no queue.
    /// </summary>
    [Fact]
    public async Task Resolves_the_item_when_the_folder_finally_appears()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedCompanyAsync(db, "42", SponsorPackage.Gold, ageDays: 3);
        var sut = Sut(db);

        await sut.RunAsync(EventId);
        Assert.Null((await db.OrganizerActionItems.SingleAsync()).ResolvedAt);

        await ProvisionAsync(db, "42");
        var after = await sut.RunAsync(EventId);

        Assert.Equal(1, after.Cleared);
        Assert.Equal(0, after.Stuck);
        var item = await db.OrganizerActionItems.SingleAsync();
        Assert.NotNull(item.ResolvedAt);
    }

    /// <summary>Re-running must refresh the one row, never pile up duplicates.</summary>
    [Fact]
    public async Task Is_idempotent_across_runs()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedCompanyAsync(db, "42", SponsorPackage.Gold, ageDays: 3);
        var sut = Sut(db);

        await sut.RunAsync(EventId);
        await sut.RunAsync(EventId);
        await sut.RunAsync(EventId);

        Assert.Single(await db.OrganizerActionItems.ToListAsync());
    }

    /// <summary>Each stuck company keeps its OWN row — one entry for all of them hides the rest.</summary>
    [Fact]
    public async Task Keeps_one_row_per_company()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedCompanyAsync(db, "42", SponsorPackage.Gold, ageDays: 3);
        await SeedCompanyAsync(db, "43", SponsorPackage.Platinum, ageDays: 5);

        var result = await Sut(db).RunAsync(EventId);

        Assert.Equal(2, result.Stuck);
        Assert.Equal(2, await db.OrganizerActionItems.CountAsync());
    }

    [Fact]
    public async Task Uses_the_locally_mirrored_company_name_when_there_is_one()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        await SeedCompanyAsync(db, "42", SponsorPackage.Gold, ageDays: 3);
        db.ErpCustomerLinks.Add(new ErpCustomerLink
        {
            EventId = EventId, SponsorCompanyId = "42", CompanyName = "Contoso A/S",
        });
        await db.SaveChangesAsync();

        await Sut(db).RunAsync(EventId);

        var item = await db.OrganizerActionItems.SingleAsync();
        Assert.Contains("Contoso A/S", item.Summary);
    }
}
