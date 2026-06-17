using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="SponsorInfoDeletionService"/> — the organizer
/// "delete stale company facts" action (REQUIREMENTS §22). It removes a
/// <see cref="SponsorInfo"/> (the one-row-per-company facts that drive the public
/// sponsors page) and is delete-safely: a facts row whose company still has an
/// active sponsor contact is refused (its public card is live). Uses the EF Core
/// InMemory provider so the real DbContext mapping + queries run, no SQL. Asserts
/// event-scoping, a not-found path, the live-company refusal, and that an orphaned
/// row is removed.
/// </summary>
public sealed class SponsorInfoDeletionServiceTests
{
    private const int EventId = 1;
    private const int OtherEventId = 2;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"sponsorinfo-del-{Guid.NewGuid():N}")
            .Options);

    private static async Task<SponsorInfo> SeedFactsAsync(
        CommunityHubDbContext db, int eventId, string companyId)
    {
        var info = new SponsorInfo
        {
            EventId = eventId, SponsorCompanyId = companyId,
            CompanyDescriptionShort = $"{companyId} blurb",
        };
        db.SponsorInfos.Add(info);
        await db.SaveChangesAsync();
        return info;
    }

    private static async Task SeedContactAsync(
        CommunityHubDbContext db, int eventId, string companyId, bool active)
    {
        db.Participants.Add(new Participant
        {
            EventId = eventId, FullName = $"{companyId} contact",
            Email = $"contact-{Guid.NewGuid():N}@example.com",
            Role = ParticipantRole.Sponsor, IsActive = active,
            SponsorCompanyId = companyId,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Delete_removes_an_orphaned_facts_row_with_no_contacts()
    {
        using var db = NewDb();
        var info = await SeedFactsAsync(db, EventId, "ACME");

        var svc = new SponsorInfoDeletionService(db);
        var result = await svc.DeleteAsync(EventId, info.Id);

        Assert.Equal(SponsorInfoDeletionService.DeletionStatus.Deleted, result.Status);
        Assert.Equal("ACME", result.SponsorCompanyId);
        Assert.False(await db.SponsorInfos.AnyAsync(s => s.Id == info.Id));
    }

    [Fact]
    public async Task Delete_removes_a_facts_row_whose_only_contact_is_inactive()
    {
        using var db = NewDb();
        var info = await SeedFactsAsync(db, EventId, "GONE");
        await SeedContactAsync(db, EventId, "GONE", active: false); // cancelled contact

        var svc = new SponsorInfoDeletionService(db);
        var result = await svc.DeleteAsync(EventId, info.Id);

        Assert.Equal(SponsorInfoDeletionService.DeletionStatus.Deleted, result.Status);
        Assert.False(await db.SponsorInfos.AnyAsync(s => s.Id == info.Id));
    }

    [Fact]
    public async Task Delete_is_blocked_while_the_company_has_an_active_contact()
    {
        using var db = NewDb();
        var info = await SeedFactsAsync(db, EventId, "LIVE");
        await SeedContactAsync(db, EventId, "LIVE", active: true);
        await SeedContactAsync(db, EventId, "LIVE", active: true);

        var svc = new SponsorInfoDeletionService(db);
        var result = await svc.DeleteAsync(EventId, info.Id);

        Assert.Equal(SponsorInfoDeletionService.DeletionStatus.Blocked, result.Status);
        Assert.Equal(2, result.ActiveContactCount);
        Assert.True(await db.SponsorInfos.AnyAsync(s => s.Id == info.Id)); // untouched
    }

    [Fact]
    public async Task Delete_reports_not_found_for_a_missing_id()
    {
        using var db = NewDb();

        var svc = new SponsorInfoDeletionService(db);
        var result = await svc.DeleteAsync(EventId, 4242);

        Assert.Equal(SponsorInfoDeletionService.DeletionStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Delete_is_edition_scoped()
    {
        using var db = NewDb();
        var theirs = await SeedFactsAsync(db, OtherEventId, "THEIRS");

        var svc = new SponsorInfoDeletionService(db);
        var result = await svc.DeleteAsync(EventId, theirs.Id);

        Assert.Equal(SponsorInfoDeletionService.DeletionStatus.NotFound, result.Status);
        Assert.True(await db.SponsorInfos.AnyAsync(s => s.Id == theirs.Id)); // untouched
    }

    [Fact]
    public async Task GetActiveContactCount_counts_only_active_in_edition_contacts_of_the_company()
    {
        using var db = NewDb();
        await SeedContactAsync(db, EventId, "CO", active: true);
        await SeedContactAsync(db, EventId, "CO", active: false);          // inactive — excluded
        await SeedContactAsync(db, EventId, "OTHER", active: true);        // other company — excluded
        await SeedContactAsync(db, OtherEventId, "CO", active: true);      // other edition — excluded

        var svc = new SponsorInfoDeletionService(db);

        Assert.Equal(1, await svc.GetActiveContactCountAsync(EventId, "CO"));
        Assert.Equal(0, await svc.GetActiveContactCountAsync(EventId, "NOBODY"));
    }
}
