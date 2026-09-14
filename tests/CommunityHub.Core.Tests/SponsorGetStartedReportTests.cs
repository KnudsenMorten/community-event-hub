using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Sponsors;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1210 — the organizer's chase list: sponsors that have not finished Get started.
///
/// <para>Operator 2026-09-12: <i>"can you give me an overview of all sponsors that have not completed
/// the get started process. i need event sponsor name + coordinator name + email"</i>.</para>
///
/// <para>🔑 The measure is <see cref="SponsorWizardService"/>'s, shared with the sponsor's own page
/// and §250's digest. These pin the SET it reports on — which is where a chase list goes wrong: chase
/// a finished sponsor and it costs goodwill, miss an unfinished one and it costs the booth.</para>
/// </summary>
public sealed class SponsorGetStartedReportTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"gs-report-{Guid.NewGuid():N}").Options);

    private static SponsorGetStartedReport NewReport(CommunityHubDbContext db)
    {
        var cmOptions = new CompanyManagerOptions();
        var wizard = new SponsorWizardService(
            db,
            new CompanyManagerClient(new HttpClient(), cmOptions),
            cmOptions,
            new EconomicContactAdminService(new NoEconomicClient()),
            NullLogger<SponsorWizardService>.Instance);

        return new SponsorGetStartedReport(db, wizard);
    }

    private static async Task SeedAsync(
        CommunityHubDbContext db,
        string companyId,
        bool isTestData = false,
        SponsorStatus status = SponsorStatus.Active,
        bool withCoordinator = true,
        string coordinatorName = "Coordinator One",
        string coordinatorEmail = "coord@example.test")
    {
        if (!await db.Events.AnyAsync(e => e.Id == EventId))
        {
            db.Events.Add(new Event
            {
                Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
                VenueName = "Bella Center", StartDate = new DateOnly(2027, 2, 9),
                EndDate = new DateOnly(2027, 2, 10), IsActive = true,
            });
        }

        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId,
            SponsorCompanyId = companyId,
            CompanyName = $"Company {companyId}",
            SponsorPackage = SponsorPackage.Gold,
            IsTestData = isTestData,
            Status = status,
            // Nothing delivered ⇒ the wizard is unfinished, which is the state under test.
        });

        if (withCoordinator)
        {
            db.Participants.Add(new Participant
            {
                EventId = EventId,
                Email = coordinatorEmail,
                FullName = coordinatorName,
                Role = ParticipantRole.Sponsor,
                SponsorCompanyId = companyId,
                IsEventCoordinator = true,
                IsActive = true,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>🔴 The three columns he asked for: sponsor, coordinator, email.</summary>
    [Fact]
    public async Task An_unfinished_sponsor_is_listed_with_its_coordinator_and_email()
    {
        using var db = NewDb();
        await SeedAsync(db, "co-1");

        var rows = await NewReport(db).NotCompletedAsync(EventId);

        var row = Assert.Single(rows);
        Assert.Equal("co-1", row.SponsorCompanyId);
        var coordinator = Assert.Single(row.Coordinators);
        Assert.Equal("Coordinator One", coordinator.Name);
        Assert.Equal("coord@example.test", coordinator.Email);
    }

    /// <summary>⚠️ A TEST company is not a sponsor to chase — the same stored flag §905 settled.</summary>
    [Fact]
    public async Task A_test_company_is_not_listed()
    {
        using var db = NewDb();
        await SeedAsync(db, "co-test", isTestData: true);

        Assert.Empty(await NewReport(db).NotCompletedAsync(EventId));
    }

    /// <summary>⚠️ And neither is one that has withdrawn.</summary>
    [Fact]
    public async Task A_withdrawn_company_is_not_listed()
    {
        using var db = NewDb();
        await SeedAsync(db, "co-gone", status: SponsorStatus.Withdrawn);

        Assert.Empty(await NewReport(db).NotCompletedAsync(EventId));
    }

    /// <summary>
    /// 🛑 §854 — a company with NO coordinator is the worst case, not an omission: the reminder
    /// emails have no recipient either, so nothing at all is chasing it. It is listed, flagged, and
    /// sorted to the top.
    /// </summary>
    [Fact]
    public async Task A_company_with_no_coordinator_is_listed_and_flagged()
    {
        using var db = NewDb();
        await SeedAsync(db, "co-orphan", withCoordinator: false);

        var row = Assert.Single(await NewReport(db).NotCompletedAsync(EventId));

        Assert.True(row.HasNobodyToChase);
        Assert.Empty(row.Coordinators);
    }

    [Fact]
    public async Task The_company_with_nobody_to_chase_sorts_first()
    {
        using var db = NewDb();
        await SeedAsync(db, "co-1");
        await SeedAsync(db, "co-orphan", withCoordinator: false);

        var rows = await NewReport(db).NotCompletedAsync(EventId);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].HasNobodyToChase, "the company nobody is chasing must be first");
    }

    /// <summary>
    /// 🔑 Every coordinator of a company is listed — a company can have several, and chasing one of
    /// three is how a request goes to the person on holiday.
    /// </summary>
    [Fact]
    public async Task Every_coordinator_of_a_company_is_listed()
    {
        using var db = NewDb();
        await SeedAsync(db, "co-1");

        db.Participants.Add(new Participant
        {
            EventId = EventId, Email = "second@example.test", FullName = "Coordinator Two",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = "co-1",
            IsEventCoordinator = true, IsActive = true,
        });
        await db.SaveChangesAsync();

        var row = Assert.Single(await NewReport(db).NotCompletedAsync(EventId));

        Assert.Equal(2, row.Coordinators.Count);
    }

    /// <summary>
    /// ⚠️ A signer-only contact is NOT a coordinator (§7c) — the company answers through its
    /// coordinator, and mailing the person who signed the contract is the wrong ask.
    /// </summary>
    [Fact]
    public async Task A_non_coordinator_contact_is_not_offered_as_someone_to_chase()
    {
        using var db = NewDb();
        await SeedAsync(db, "co-1", withCoordinator: false);

        db.Participants.Add(new Participant
        {
            EventId = EventId, Email = "signer@example.test", FullName = "The Signer",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = "co-1",
            IsEventCoordinator = false, IsActive = true,
        });
        await db.SaveChangesAsync();

        var row = Assert.Single(await NewReport(db).NotCompletedAsync(EventId));

        Assert.True(row.HasNobodyToChase);
    }

    /// <summary>The progress column's arithmetic, including the 0-step case that must not divide.</summary>
    [Fact]
    public void Percent_done_is_out_of_the_EVALUABLE_steps()
    {
        var row = new SponsorGetStartedRow(
            "co-1", "Company", Array.Empty<SponsorCoordinator>(), 3, 4, ["logos"]);

        Assert.Equal(75, row.PercentDone);

        var nothing = new SponsorGetStartedRow(
            "co-2", "Company", Array.Empty<SponsorCoordinator>(), 0, 0, []);

        Assert.Equal(0, nothing.PercentDone);
    }

    /// <summary>e-conomic is never reached here; the sponsor wizard just needs an instance.</summary>
    private sealed class NoEconomicClient : IEconomicContactAdminClient
    {
        public bool CanWrite => false;
        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(Array.Empty<EconomicCustomerRow>());
        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(Array.Empty<EconomicContactRow>());
        public Task<int> CreateContactAsync(
            int customerNumber, EconomicContactInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateContactAsync(
            int customerNumber, int contactNumber, EconomicContactInput input,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteContactAsync(
            int customerNumber, int contactNumber, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
