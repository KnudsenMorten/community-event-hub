using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1112 — a webshop company whose e-conomic customer has LEFT the sponsor group.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-21: *"this is a erp customer that changed from customer group 1 (sponsor)
/// to 2 (customer), so it must be removed or deactivated from ceh. it came in because it was created
/// as a sponsor but it is a attendee company"*.</para>
///
/// <para>🔑 <b>Leaving a list is not an event.</b> The reconcile enumerates group 1, so a customer
/// moved to group 2 just stops appearing — no note, no change, and CEH goes on treating an attendee
/// company as a sponsor for ever. These tests pin the detection AND, just as hard, the three cases
/// where it must NOT act.</para>
///
/// <para>FAKE company names only.</para>
/// </remarks>
public sealed class ErpWebshopDeSponsoredSweepTests
{
    private const int EventId = 88;

    /// <summary>Company 1 ↔ ERP #100 (still a sponsor), company 2 ↔ ERP #200 (the leaver).</summary>
    private sealed class CmHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string body) =>
                new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            if (req.Method == HttpMethod.Get && path.EndsWith("/companies"))
                return Task.FromResult(Json(
                    "[{\"id\":1,\"erp_customer_number\":\"100\",\"name\":\"Alpha\",\"default_signer_id\":0,\"event_coordination_default_contact_id\":0},"
                    + "{\"id\":2,\"erp_customer_number\":\"200\",\"name\":\"Beta\",\"default_signer_id\":0,\"event_coordination_default_contact_id\":0}]"));

            if (req.Method == HttpMethod.Get && path.Contains("/companies/") && path.EndsWith("/users"))
                return Task.FromResult(Json("[]"));

            if (req.Method == HttpMethod.Post && path.EndsWith("/users"))
                return Task.FromResult(Json("{\"user_id\":999}"));

            return Task.FromResult(Json("{}"));
        }
    }

    /// <summary>
    /// e-conomic with a configurable split: which numbers are in group 1, and which exist AT ALL.
    /// </summary>
    private sealed class StubErp : IEconomicContactAdminClient
    {
        private readonly int[] _sponsors;
        private readonly int[] _allCustomers;
        private readonly bool _fullListThrows;

        public StubErp(int[] sponsors, int[] allCustomers, bool fullListThrows = false)
        {
            _sponsors = sponsors;
            _allCustomers = allCustomers;
            _fullListThrows = fullListThrows;
        }

        public bool CanWrite => true;

        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default)
        {
            // customerGroup == 1 ⇒ the sponsor group. null ⇒ every customer, any group.
            if (customerGroup is null && _fullListThrows)
                throw new HttpRequestException("e-conomic is down");

            var set = customerGroup is null ? _allCustomers : _sponsors;
            return Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(
                set.Select(n => new EconomicCustomerRow(n, $"Customer {n}", null)).ToList());
        }

        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(new[]
            {
                new EconomicContactRow(1, $"Person {customerNumber}", $"p{customerNumber}@x.test", "+45", "Role:1,2"),
            });

        public Task<int> CreateContactAsync(int c, EconomicContactInput i, CancellationToken ct = default) => Task.FromResult(1);
        public Task UpdateContactAsync(int c, int n, EconomicContactInput i, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteContactAsync(int c, int n, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopEmail : IEmailSender
    {
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"desponsor-{Guid.NewGuid():N}").Options);

    /// <summary>Company 2 (ERP #200) as a CEH sponsor with one contact and, optionally, a booth.</summary>
    private static async Task SeedAsync(
        CommunityHubDbContext db, bool established = false, bool withSponsorInfo = true)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "DS27", CommunityName = "C", DisplayName = "DS 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });

        if (withSponsorInfo)
        {
            db.SponsorInfos.Add(new SponsorInfo
            {
                EventId = EventId, SponsorCompanyId = "2", CompanyName = "Beta",
                // "Established" = a Zoho sponsor record and a booth — a company that is really coming.
                ZohoSponsorId = established ? "zoho-999" : null,
                Tier = established ? BoothTier.Gold : BoothTier.None,
                IsExhibitor = established,
            });
        }

        db.Participants.Add(new Participant
        {
            EventId = EventId, FullName = "Beta Person", Email = "p200@x.test",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = "2", IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private static ErpWebshopContactSyncService Sut(
        CommunityHubDbContext db, IEconomicContactAdminClient erp)
    {
        var cm = new CompanyManagerClient(new HttpClient(new CmHandler()), new CompanyManagerOptions
        {
            Enabled = true, BaseUrl = "https://cm.test/wp-json/company-manager/v1",
            Username = "u", Password = "p",
        });
        var clock = new FixedClock();
        return new ErpWebshopContactSyncService(
            new EconomicContactAdminService(erp), cm,
            new CompanyManagerOptions { Enabled = true }, new NoopEmail(),
            NullLogger<ErpWebshopContactSyncService>.Instance,
            db: db,
            deactivate: new ParticipantDeactivationService(db, clock, new CommunityHub.Core.Audit.AuditTrailService(db, clock)));
    }

    // ---------------------------------------------------------------------
    //  It acts
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_company_that_left_the_sponsor_group_is_withdrawn_and_its_contacts_deactivated()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // #200 is no longer in group 1, but it is still very much an e-conomic customer.
        var result = await Sut(db, new StubErp(sponsors: new[] { 100 }, allCustomers: new[] { 100, 200 }))
            .SyncAsync();

        Assert.Equal(1, result.DeSponsoredWithdrawn);

        var info = await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "2");
        Assert.Equal(SponsorStatus.Withdrawn, info.Status);
        Assert.False(await db.Participants.AnyAsync(p => p.SponsorCompanyId == "2" && p.IsActive));

        var note = Assert.Single(result.AlertNotes, n => n.Contains("WITHDRAWN", StringComparison.Ordinal));
        Assert.Contains("200", note);
        // 🔑 It says what is left for a human: CEH cannot unlink a Company Manager company (§505).
        Assert.Contains("Company Manager", note);
        Assert.Contains("Reversible", note);
    }

    [Fact]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var erp = new StubErp(sponsors: new[] { 100 }, allCustomers: new[] { 100, 200 });

        Assert.Equal(1, (await Sut(db, erp).SyncAsync()).DeSponsoredWithdrawn);

        var second = await Sut(db, erp).SyncAsync();
        Assert.Equal(0, second.DeSponsoredWithdrawn);
        // 🔒 And it does not keep re-reporting a company it already dealt with — a note that repeats
        // every 10 minutes is how a real one gets missed.
        Assert.DoesNotContain(second.AlertNotes, n => n.Contains("WITHDRAWN", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------
    //  It refuses to act — the three cases that matter more than the one above
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_customer_missing_from_e_conomic_ENTIRELY_is_reported_but_never_withdrawn()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // #200 is in NO group — deleted, renumbered, or simply unreadable. All three look identical
        // from here and only one of them means "stopped being a sponsor".
        var result = await Sut(db, new StubErp(sponsors: new[] { 100 }, allCustomers: new[] { 100 }))
            .SyncAsync();

        Assert.Equal(0, result.DeSponsoredWithdrawn);
        Assert.Equal(SponsorStatus.Active,
            (await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "2")).Status);
        Assert.True(await db.Participants.AnyAsync(p => p.SponsorCompanyId == "2" && p.IsActive));

        var note = Assert.Single(result.AlertNotes, n => n.Contains("NOT IN ERP", StringComparison.Ordinal));
        Assert.Contains("Nothing was changed", note);
    }

    [Fact]
    public async Task An_unreadable_customer_list_withdraws_nobody_and_says_nothing()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var result = await Sut(db,
            new StubErp(sponsors: new[] { 100 }, allCustomers: new[] { 100, 200 }, fullListThrows: true))
            .SyncAsync();

        Assert.Equal(0, result.DeSponsoredWithdrawn);
        Assert.Equal(SponsorStatus.Active,
            (await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "2")).Status);
        // 🔒 No note either: a note on every e-conomic blip would describe a departure that may not
        // have happened, and this mail is the one he actually reads.
        Assert.DoesNotContain(result.AlertNotes,
            n => n.Contains("WITHDRAWN", StringComparison.Ordinal)
                 || n.Contains("NOT IN ERP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_ESTABLISHED_sponsor_is_reported_for_his_decision_not_withdrawn_automatically()
    {
        using var db = NewDb();
        await SeedAsync(db, established: true);

        var result = await Sut(db, new StubErp(sponsors: new[] { 100 }, allCustomers: new[] { 100, 200 }))
            .SyncAsync();

        // ⚠️ Withdrawing deactivates every contact and cancels their party seats. On a company with a
        // Zoho sponsor record and a booth, the likeliest cause of a group change is a mis-click.
        Assert.Equal(0, result.DeSponsoredWithdrawn);
        Assert.Equal(SponsorStatus.Active,
            (await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "2")).Status);
        Assert.True(await db.Participants.AnyAsync(p => p.SponsorCompanyId == "2" && p.IsActive));

        var note = Assert.Single(result.AlertNotes, n => n.Contains("needs your decision", StringComparison.Ordinal));
        Assert.Contains("Nothing was changed", note);
    }

    [Fact]
    public async Task A_single_company_re_run_never_sweeps_the_estate()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // 🔴 With onlyCustomerNumber set, the group-1 list narrows to one — so without the guard
        // every OTHER sponsor would look like it had left the group.
        var result = await Sut(db, new StubErp(sponsors: new[] { 100, 200 }, allCustomers: new[] { 100, 200 }))
            .SyncAsync(onlyCustomerNumber: 100);

        Assert.Equal(0, result.DeSponsoredWithdrawn);
        Assert.Equal(SponsorStatus.Active,
            (await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "2")).Status);
    }

    [Fact]
    public async Task A_leaver_with_no_CEH_footprint_is_reported_with_the_webshop_steps_only()
    {
        using var db = NewDb();
        // No SponsorInfo, no contacts — his actual case: created as a sponsor, never really one.
        db.Events.Add(new Event
        {
            Id = EventId, Code = "DS27", CommunityName = "C", DisplayName = "DS 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await Sut(db, new StubErp(sponsors: new[] { 100 }, allCustomers: new[] { 100, 200 }))
            .SyncAsync();

        Assert.Equal(0, result.DeSponsoredWithdrawn);
        var note = Assert.Single(result.AlertNotes,
            n => n.Contains("NO LONGER A SPONSOR", StringComparison.Ordinal));
        Assert.Contains("Nothing to withdraw in CEH", note);
        Assert.Contains("erp_customer_number", note);
    }
}
