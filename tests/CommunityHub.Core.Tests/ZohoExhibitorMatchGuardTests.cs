using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1312 — a company's Zoho EXHIBITOR id is bootstrapped by name with the same guard as the sponsor record (§1159):
/// never a record another company already holds, and nothing linked when several unclaimed records share the name.
/// </summary>
/// <remarks>
/// It took the FIRST exhibitor with the name. Two booth companies with one public name (a group's country entities, as
/// the operator set up on 2026-08-31) would then both point at one exhibitor, and every sync would overwrite the other's
/// booth members, description and links. Operator 2026-09-27: "yes add it to bundle 3". FAKE names only; fully offline.
/// </remarks>
public sealed class ZohoExhibitorMatchGuardTests
{
    private sealed class ExhibitorListHandler(string exhibitorsJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/oauth/v2/token", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json("{\"access_token\":\"stub-token\"}"));
            if (request.Method == HttpMethod.Get && path.EndsWith("/exhibitors", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json(exhibitorsJson));
            return Task.FromResult(Json("{\"booth_members\":[],\"exhibitors\":[]}"));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static SponsorZohoSyncService NewSync(CommunityHubDbContext db, HttpMessageHandler handler)
    {
        var zohoOptions = new ZohoOptions
        {
            Enabled = true, ApiDomain = "https://stub.local", TokenEndpoint = "https://stub.local/oauth/v2/token",
            BackstagePortalId = "p1", BackstageEventId = "e1", ClientId = "cid", ClientSecret = "secret", RefreshToken = "refresh",
        };
        return new SponsorZohoSyncService(
            new ZohoClient(new HttpClient(handler), zohoOptions, NullLogger<ZohoClient>.Instance),
            db, zohoOptions,
            NullLogger<SponsorZohoSyncService>.Instance);
    }

    private static async Task<int> SeedAsync(CommunityHubDbContext db, params SponsorInfo[] infos)
    {
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "XM27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        foreach (var i in infos) { i.EventId = ev.Id; db.SponsorInfos.Add(i); }
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static SponsorInfo Booth(string id, string? exhibitorId = null) => new()
    {
        SponsorCompanyId = id, CompanyName = "Group", SponsorPackage = SponsorPackage.Gold, IsSponsor = true,
        ZohoExhibitorId = exhibitorId,
    };

    private static async Task<string?> ExhibitorIdAsync(CommunityHubDbContext db, string companyId) =>
        (await db.SponsorInfos.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.SponsorCompanyId == companyId)).ZohoExhibitorId;

    [Fact]
    public async Task A_record_another_company_holds_is_not_taken()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedAsync(db, Booth("dk", "ZEX-1"), Booth("no"));
        var handler = new ExhibitorListHandler("{\"exhibitors\":[{\"id\":\"ZEX-1\",\"company_name\":\"Group\"}]}");

        await NewSync(db, handler).SyncBoothMembersAsync(eventId, "no", "Group");

        Assert.Null(await ExhibitorIdAsync(db, "no"));           // the old FirstOrDefault took ZEX-1 here
        Assert.Equal("ZEX-1", await ExhibitorIdAsync(db, "dk"));
    }

    [Fact]
    public async Task Several_unclaimed_records_with_the_name_link_nothing()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedAsync(db, Booth("no"));
        var handler = new ExhibitorListHandler(
            "{\"exhibitors\":[{\"id\":\"ZEX-1\",\"company_name\":\"Group\"},{\"id\":\"ZEX-2\",\"company_name\":\"Group\"}]}");

        await NewSync(db, handler).SyncBoothMembersAsync(eventId, "no", "Group");

        Assert.Null(await ExhibitorIdAsync(db, "no"));
    }

    [Fact]
    public async Task One_unclaimed_record_with_the_name_is_linked_as_before()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedAsync(db, Booth("dk", "ZEX-1"), Booth("no"));
        var handler = new ExhibitorListHandler(
            "{\"exhibitors\":[{\"id\":\"ZEX-1\",\"company_name\":\"Group\"},{\"id\":\"ZEX-2\",\"company_name\":\"Group\"}]}");

        await NewSync(db, handler).SyncBoothMembersAsync(eventId, "no", "Group");

        Assert.Equal("ZEX-2", await ExhibitorIdAsync(db, "no"));  // ZEX-1 is taken, so ZEX-2 is the only candidate
    }
}
