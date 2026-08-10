using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1034b — A COUPON CUSTOMER'S CONTACTS ARE NOT SPONSOR PARTICIPANTS.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10: <i>"this is a coupon customer, and must not be created as
/// sponsor"</i>.</para>
///
/// <para>🔴 <b>The gate is HERE because this is what stamps <c>Role = Sponsor</c>.</b> §1034 first
/// put it at the order-pull call site, and measurement the same hour showed that was the one caller
/// that was innocent: the reported company has <b>no completed webshop order at all</b> — the pull
/// ran on the new code and created nothing for it. It arrived through one of the other three
/// callers, all organizer-side (/Organizer/EconomicContacts, the sponsor-admin dashboard,
/// CompanyDetails).</para>
///
/// <para>🔑 <b>"No row" means "not a sponsor" for this question.</b> A real sponsor always has one
/// by the time contacts are mirrored — the order pull writes it before the contact loop, and the
/// organizer/sponsor forms write it with <c>IsSponsor = true</c>. A company with no row is one
/// nothing has ever classified, which is exactly what an e-conomic coupon customer is.</para>
/// </remarks>
public sealed class SponsorContactSyncNonSponsorGateTests
{
    private const int EventId = 1;
    private const int CompanyId = 33;          // the reported coupon customer

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"nonsponsorgate-{Guid.NewGuid():N}").Options);

    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var json = request.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)
                ? """
                  [ { "user_id": "1", "user_email": "arimo@example.test", "full_name": "Arimo P" },
                    { "user_id": "2", "user_email": "kaisa@example.test", "full_name": "Kaisa M" } ]
                  """
                : """{ "id": 33, "name": "Coupon Customer A/S" }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (SponsorContactSyncService Svc, StubHandler Handler) NewSync(CommunityHubDbContext db)
    {
        var options = new CompanyManagerOptions
        {
            Enabled = true,
            BaseUrl = "https://example.test/wp-json/company-manager/v1",
            Username = "u",
            Password = "p",
        };
        var handler = new StubHandler();
        var svc = new SponsorContactSyncService(
            db, new CompanyManagerClient(new HttpClient(handler), options), options,
            Scenario.ScenarioFixture.Clock, NullLogger<SponsorContactSyncService>.Instance);
        return (svc, handler);
    }

    private static async Task SeedEventAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>🔴 The reported case: an e-conomic customer with no sponsor row at all.</summary>
    [Fact]
    public async Task A_company_with_no_sponsor_row_gets_no_sponsor_participants()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var (svc, handler) = NewSync(db);

        var result = await svc.SyncCompanyAsync(EventId, CompanyId);

        Assert.Equal(0, result.ParticipantsCreated);
        Assert.Empty(await db.Participants.ToListAsync());
        // 🔒 It does not even ASK Company Manager — the answer is knowable without the round trip.
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>A company the pull classified as buying no sponsorship is refused the same way.</summary>
    [Fact]
    public async Task A_company_flagged_IsSponsor_false_gets_no_sponsor_participants()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = CompanyId.ToString(),
            CompanyName = "Coupon Customer A/S", IsSponsor = false,
        });
        await db.SaveChangesAsync();
        var (svc, _) = NewSync(db);

        var result = await svc.SyncCompanyAsync(EventId, CompanyId);

        Assert.Equal(0, result.ParticipantsCreated);
        Assert.Empty(await db.Participants.ToListAsync());
    }

    /// <summary>
    /// ⚠️ The positive case, without which this gate would be indistinguishable from "the mirror is
    /// broken": a real sponsor still gets its contacts.
    /// </summary>
    [Fact]
    public async Task A_real_sponsors_contacts_are_still_mirrored()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = CompanyId.ToString(),
            CompanyName = "Real Sponsor A/S", IsSponsor = true,
        });
        await db.SaveChangesAsync();
        var (svc, _) = NewSync(db);

        var result = await svc.SyncCompanyAsync(EventId, CompanyId);

        Assert.Equal(2, result.ParticipantsCreated);
        Assert.All(
            await db.Participants.ToListAsync(),
            p => Assert.Equal(ParticipantRole.Sponsor, p.Role));
    }

    /// <summary>
    /// 🔒 Edition-scoped: a sponsor row in ANOTHER edition does not open the gate for this one.
    /// Company ids are reused across editions, and a stale row would let a coupon customer through
    /// for ever.
    /// </summary>
    [Fact]
    public async Task A_sponsor_row_in_another_edition_does_not_open_the_gate()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Events.Add(new Event
        {
            Id = 2, Code = "ELDK28", CommunityName = "ELDK", DisplayName = "ELDK 2028",
            StartDate = new DateOnly(2028, 2, 9), EndDate = new DateOnly(2028, 2, 10),
        });
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = 2, SponsorCompanyId = CompanyId.ToString(),
            CompanyName = "Real Sponsor A/S", IsSponsor = true,
        });
        await db.SaveChangesAsync();
        var (svc, _) = NewSync(db);

        var result = await svc.SyncCompanyAsync(EventId, CompanyId);

        Assert.Equal(0, result.ParticipantsCreated);
        Assert.Empty(await db.Participants.ToListAsync());
    }
}
