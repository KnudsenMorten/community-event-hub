using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §32 sponsor "Get started" wizard — steps from the Company Details sections, in
/// order, entitlement-aware (booth steps for exhibitors only), with hub-tracked
/// completion (SponsorInfo + booth members/materials). The ERP-contacts step is
/// tracked from e-conomic when that integration is configured, else shown as a
/// guided (undeterminable / untracked) link. Every step is numbered + counted so the
/// "Continue — step X of Y" line always matches the displayed list.
/// </summary>
public sealed class SponsorWizardServiceTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"spwiz-{System.Guid.NewGuid()}").Options);

    /// <summary>An e-conomic contact client that is not configured (CanWrite=false).</summary>
    private sealed class OfflineErpClient : IEconomicContactAdminClient
    {
        public bool CanWrite => false;
        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(string? s, int? g = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(new List<EconomicCustomerRow>());
        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EconomicContactRow>>(new List<EconomicContactRow>());
        public Task<int> CreateContactAsync(int c, EconomicContactInput i, CancellationToken ct = default) => Task.FromResult(0);
        public Task UpdateContactAsync(int c, int n, EconomicContactInput i, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteContactAsync(int c, int n, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Service with the e-conomic / Company Manager integration DISABLED, so the
    /// contacts step is left undeterminable (null) — the fail-soft path. The contacts
    /// check short-circuits on disabled options before touching the HTTP clients.
    /// </summary>
    private static SponsorWizardService OfflineSvc(CommunityHubDbContext db) =>
        new(db,
            new CompanyManagerClient(new HttpClient(), new CompanyManagerOptions { Enabled = false }),
            new CompanyManagerOptions { Enabled = false },
            new EconomicContactAdminService(new OfflineErpClient()),
            NullLogger<SponsorWizardService>.Instance);

    private static async Task<(CommunityHubDbContext db, int ev, int pid)> SeedAsync(
        SponsorInfo? info, string companyId = "c1")
    {
        var db = NewDb();
        var e = new Event { Code = "e", DisplayName = "E", CommunityName = "C", IsActive = true };
        db.Events.Add(e);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = e.Id, FullName = "Sponsor One", Email = "s@x.dk",
            Role = ParticipantRole.Sponsor, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active, SponsorCompanyId = companyId,
        };
        db.Participants.Add(p);
        if (info is not null) { info.EventId = e.Id; info.SponsorCompanyId = companyId; db.SponsorInfos.Add(info); }
        await db.SaveChangesAsync();
        return (db, e.Id, p.Id);
    }

    [Fact]
    public async Task No_company_link_returns_null()
    {
        var (db, ev, pid) = await SeedAsync(info: null, companyId: "");
        Assert.Null(await OfflineSvc(db).BuildAsync(ev, pid));
    }

    [Fact]
    public async Task Non_exhibitor_has_four_steps_and_tracks_completion()
    {
        var (db, ev, pid) = await SeedAsync(new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Silver,   // no booth
            WebsiteUrl = "https://2linkit.net",       // details done
            // no coordinator, no logo
        });

        var v = (await OfflineSvc(db).BuildAsync(ev, pid))!;

        Assert.Equal(new[] { "details", "coordinator", "contacts", "logos" }, v.Steps.Select(s => s.Key).ToArray());
        Assert.True(v.Steps.Single(s => s.Key == "details").Done);
        Assert.False(v.Steps.Single(s => s.Key == "coordinator").Done);
        Assert.Null(v.Steps.Single(s => s.Key == "contacts").Done);   // e-conomic off → undeterminable
        Assert.False(v.Steps.Single(s => s.Key == "logos").Done);
        Assert.Equal(4, v.TotalSteps);                                // every step numbered/counted
        Assert.Equal(1, v.DoneCount);
        Assert.Equal("coordinator", v.NextStep!.Key);
        Assert.False(v.AllDone);
    }

    [Fact]
    public async Task Exhibitor_adds_booth_steps_with_data_backed_completion()
    {
        var (db, ev, pid) = await SeedAsync(new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Gold,     // has booth
            WebsiteUrl = "https://2linkit.net",
            EventCoordinatorEmail = "coord@x.dk",
            LogoRasterPath = "/logo.png",
        });
        db.SponsorBoothMembers.Add(new SponsorBoothMember
        { EventId = ev, SponsorCompanyId = "c1", FirstName = "A", LastName = "B", Email = "a@b.dk" });
        await db.SaveChangesAsync();

        var v = (await OfflineSvc(db).BuildAsync(ev, pid))!;

        Assert.Equal(new[] { "details", "coordinator", "contacts", "logos", "booth-members", "booth-materials" },
            v.Steps.Select(s => s.Key).ToArray());
        Assert.True(v.Steps.Single(s => s.Key == "booth-members").Done);    // a member exists
        Assert.False(v.Steps.Single(s => s.Key == "booth-materials").Done); // none yet
        // details + coordinator + logos + booth-members done; booth-materials not.
        Assert.Equal(6, v.TotalSteps);
        Assert.Equal(4, v.DoneCount);
        Assert.Equal("booth-materials", v.NextStep!.Key);
    }

    /// <summary>
    /// Regression guard for the "next not completed is #3 but it refers to #4" bug:
    /// the "Continue" target's number MUST equal its 1-based position in the displayed
    /// list, and the denominator MUST be the total number of steps shown — not the old
    /// tracked-only count (which produced "step 4 of 3"-style mismatches when the
    /// untracked contacts step sat in the middle).
    /// </summary>
    [Fact]
    public async Task NextStepNumber_matches_list_position_and_total()
    {
        var (db, ev, pid) = await SeedAsync(new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Silver,   // no booth → 4 steps
            WebsiteUrl = "https://2linkit.net",       // details done
            EventCoordinatorEmail = "coord@x.dk",     // coordinator done
            // contacts untracked (e-conomic off), logos NOT done
        });

        var v = (await OfflineSvc(db).BuildAsync(ev, pid))!;

        // Steps: 1 details(✓) 2 coordinator(✓) 3 contacts(untracked) 4 logos(✗).
        Assert.Equal("logos", v.NextStep!.Key);
        var listPosition = v.Steps.Select((s, i) => (s, i)).First(x => x.s.Key == v.NextStep.Key).i + 1;
        Assert.Equal(listPosition, v.NextStepNumber);   // number shown == list item the user sees
        Assert.Equal(4, v.NextStepNumber);
        Assert.Equal(4, v.TotalSteps);                  // "step 4 of 4", not "4 of 3"
        Assert.True(v.NextStepNumber <= v.TotalSteps);
    }
}
