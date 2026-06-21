using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Verifies the unique-identifier contact link (REQUIREMENTS §7c):
/// <see cref="SponsorContactSyncService"/> WRITES <see cref="Participant.CmUserId"/>
/// from the Company Manager user's <c>user_id</c> on BOTH create and update of a
/// sponsor contact. The contact is linked to CM by id, never by name. Drives the
/// REAL <see cref="CompanyManagerClient"/> through a fake HTTP handler so the
/// tolerant <c>user_id</c> parse (CM sends it as a STRING "68" on
/// <c>/companies/{id}/users</c>) is exercised end-to-end.
/// </summary>
public sealed class SponsorContactSyncCmUserIdTests
{
    private const int EventId = 1;
    private const int CompanyId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"cmuserid-{Guid.NewGuid():N}")
            .Options);

    /// <summary>A fake handler returning canned JSON per request path.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _byPath;
        public StubHandler(Func<string, string> byPath) => _byPath = byPath;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = _byPath(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (SponsorContactSyncService Svc, CommunityHubDbContext Db) NewSync(
        CommunityHubDbContext db, Func<string, string> respond)
    {
        var options = new CompanyManagerOptions
        {
            Enabled = true,
            BaseUrl = "https://example.test/wp-json/company-manager/v1",
            Username = "u",
            Password = "p",
        };
        var http = new HttpClient(new StubHandler(respond));
        var cm = new CompanyManagerClient(http, options);
        var svc = new SponsorContactSyncService(
            db, cm, options, Scenario.ScenarioFixture.Clock,
            NullLogger<SponsorContactSyncService>.Instance);
        return (svc, db);
    }

    // CM returns user_id as a STRING on /companies/{id}/users (the real shape).
    private static string UsersJson() => """
        [
          { "user_id": "68", "user_email": "coord@2linkit.net", "full_name": "Coord Person", "display_name": "Coord" },
          { "user_id": "77", "user_email": "signer@2linkit.net", "full_name": "Signer Person", "display_name": "Signer" }
        ]
        """;

    private static string CompanyJson(int signerId = 0, int coordinatorId = 0) => $$"""
        {
          "id": {{CompanyId}},
          "name": "2linkIT ApS",
          "company_name_public": "2LINKIT",
          "default_signer_id": {{signerId}},
          "event_coordination_default_contact_id": {{coordinatorId}}
        }
        """;

    private static Func<string, string> Respond(int signerId = 0, int coordinatorId = 0) =>
        path => path.EndsWith("/users", StringComparison.Ordinal)
            ? UsersJson()
            : CompanyJson(signerId, coordinatorId);

    private static async Task<Event> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "Experts Live Denmark",
            DisplayName = "Experts Live Denmark 2027",
            Code = "ELDK27",
            IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev;
    }

    [Fact]
    public async Task Create_writes_CmUserId_from_user_id_string()
    {
        using var db = NewDb();
        var ev = await SeedEventAsync(db);
        var (svc, _) = NewSync(db, Respond());

        var result = await svc.SyncCompanyAsync(ev.Id, CompanyId);
        Assert.Equal(2, result.ParticipantsCreated);

        var coord = await db.Participants.SingleAsync(p => p.Email == "coord@2linkit.net");
        var signer = await db.Participants.SingleAsync(p => p.Email == "signer@2linkit.net");

        // The CM user_id ("68"/"77") is parsed and persisted as the id-link.
        Assert.Equal(68, coord.CmUserId);
        Assert.Equal(77, signer.CmUserId);
    }

    [Fact]
    public async Task Update_backfills_CmUserId_on_a_preexisting_sponsor_row()
    {
        using var db = NewDb();
        var ev = await SeedEventAsync(db);

        // A sponsor row created before the id-link existed (CmUserId null).
        db.Participants.Add(new Participant
        {
            EventId = ev.Id,
            Email = "coord@2linkit.net",
            FullName = "Coord Person",
            Role = ParticipantRole.Sponsor,
            SponsorCompanyId = CompanyId.ToString(),
            CmUserId = null,
        });
        await db.SaveChangesAsync();

        var (svc, _) = NewSync(db, Respond());
        var result = await svc.SyncCompanyAsync(ev.Id, CompanyId);

        Assert.Equal(1, result.ParticipantsCreated); // the signer is new
        Assert.Equal(1, result.ParticipantsUpdated); // the coord row backfilled

        var coord = await db.Participants.SingleAsync(p => p.Email == "coord@2linkit.net");
        Assert.Equal(68, coord.CmUserId);
    }

    [Fact]
    public async Task Resync_is_idempotent_and_keeps_CmUserId()
    {
        using var db = NewDb();
        var ev = await SeedEventAsync(db);
        var (svc, _) = NewSync(db, Respond());

        await svc.SyncCompanyAsync(ev.Id, CompanyId);
        var second = await svc.SyncCompanyAsync(ev.Id, CompanyId);

        // Nothing changes on the second run (CmUserId already matches).
        Assert.Equal(0, second.ParticipantsCreated);
        Assert.Equal(0, second.ParticipantsUpdated);

        var coord = await db.Participants.SingleAsync(p => p.Email == "coord@2linkit.net");
        Assert.Equal(68, coord.CmUserId);
        Assert.Equal(2, await db.Participants.CountAsync()); // no duplicate rows
    }

    [Fact]
    public async Task Sync_does_not_correlate_by_name_only_by_id_and_company()
    {
        // A pre-existing sponsor row with the SAME full name but a DIFFERENT email
        // must NOT be matched to the CM user — correlation is by id/email, never by
        // name. The CM users create their own rows; the name-twin keeps null CmUserId.
        using var db = NewDb();
        var ev = await SeedEventAsync(db);

        db.Participants.Add(new Participant
        {
            EventId = ev.Id,
            Email = "different.address@elsewhere.test",
            FullName = "Coord Person",                 // same NAME as the CM coord
            Role = ParticipantRole.Sponsor,
            SponsorCompanyId = CompanyId.ToString(),
            CmUserId = null,
        });
        await db.SaveChangesAsync();

        var (svc, _) = NewSync(db, Respond());
        await svc.SyncCompanyAsync(ev.Id, CompanyId);

        // The name-twin was never touched (no name-based match).
        var twin = await db.Participants.SingleAsync(p => p.Email == "different.address@elsewhere.test");
        Assert.Null(twin.CmUserId);

        // The real CM coord got its own row, linked by its CM user_id.
        var coord = await db.Participants.SingleAsync(p => p.Email == "coord@2linkit.net");
        Assert.Equal(68, coord.CmUserId);
    }
}
