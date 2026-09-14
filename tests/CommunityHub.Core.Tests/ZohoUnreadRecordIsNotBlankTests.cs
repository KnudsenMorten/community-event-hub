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
/// 🔴 §1220 — a Zoho record we could not READ is not a record whose fields are blank.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-14, on "[CEH→Zoho] Sponsors / exhibitors: 4 change(s)" listing Website for
/// four sponsors: <i>"this mail keeps coming, something is wrong with the comparison, as the fields
/// ARE already set"</i>. Zoho already held the exact webshop value for all four.</para>
///
/// <para>PROD telemetry showed the mechanism: Zoho answered bursts of sponsor GETs with HTTP 400,
/// the reader returned null, NeedsManualEntry scored null as blank, and every PUT that day followed a
/// failed read of the same record by milliseconds. The next good read then cleared the §1175 ledger,
/// so the announcement could never go quiet.</para>
///
/// <para>FAKE names only. Fully offline.</para>
/// </remarks>
public sealed class ZohoUnreadRecordIsNotBlankTests
{
    private sealed record ZohoCall(HttpMethod Method, string Path);

    /// <summary>Zoho that refuses every GET-by-id with 400 — the PROD burst.</summary>
    private sealed class RefusingReadsHandler : HttpMessageHandler
    {
        public List<ZohoCall> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(new ZohoCall(request.Method, path));

            if (path.Contains("/oauth/v2/token", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json("{\"access_token\":\"stub-token\"}"));
            if (request.Method == HttpMethod.Put)
                return Task.FromResult(Json("{\"ok\":true}"));
            if (request.Method == HttpMethod.Get
                && (path.Contains("/sponsors/", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("/exhibitors/", StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"message\":\"bad request\"}", Encoding.UTF8, "application/json"),
                });

            return Task.FromResult(Json("{\"sponsors\":[],\"exhibitors\":[],\"sponsorship_types\":[],\"booths\":[]}"));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static SponsorZohoSyncService NewSync(CommunityHubDbContext db, HttpMessageHandler handler)
    {
        var zohoOptions = new ZohoOptions
        {
            Enabled = true,
            ApiDomain = "https://stub.local",
            TokenEndpoint = "https://stub.local/oauth/v2/token",
            BackstagePortalId = "p1",
            BackstageEventId = "e1",
            ClientId = "cid",
            ClientSecret = "secret",
            RefreshToken = "refresh",
        };
        var cmOptions = new CompanyManagerOptions { Enabled = false };
        return new SponsorZohoSyncService(
            new ZohoClient(new HttpClient(handler), zohoOptions, NullLogger<ZohoClient>.Instance),
            db, zohoOptions, new CompanyManagerClient(new HttpClient(handler), cmOptions), cmOptions,
            NullLogger<SponsorZohoSyncService>.Instance);
    }

    private static async Task<int> SeedAsync(CommunityHubDbContext db, SponsorInfo info)
    {
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "UR27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        info.EventId = ev.Id;
        db.SponsorInfos.Add(info);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private static SponsorInfo LinkedBoothCompany() => new()
    {
        SponsorCompanyId = "10",
        SponsorPackage = SponsorPackage.Gold,            // booth → exhibitor record too
        ZohoSponsorId = "ZSP-10",
        ZohoExhibitorId = "ZEX-10",
        WebsiteUrl = "https://www.company-ten.example",
        CompanyDescription = "We make widgets.",
        LinkedInUrl = "https://linkedin.com/company/ten",
        EventCoordinatorEmail = "coord@example.com",
        ZohoContactEmail = "coord@example.com",
    };

    [Fact]
    public async Task An_unreadable_record_is_neither_pushed_nor_announced()
    {
        using var db = ScenarioFixture.NewDb();
        var handler = new RefusingReadsHandler();
        var eventId = await SeedAsync(db, LinkedBoothCompany());

        var result = await NewSync(db, handler).SyncAsync(eventId, "10", "Company Ten", notifyZohoChange: false);

        // 🔑 The reported bug: a PUT, announced as "Website", after a read that failed.
        Assert.DoesNotContain(handler.Calls, c => c.Method == HttpMethod.Put);
        Assert.Empty(result.SponsorFields ?? Array.Empty<string>());
        Assert.Empty(result.ExhibitorFields ?? Array.Empty<string>());
    }

    /// <summary>Records read fine (blank in Zoho); every PUT gets Zoho's generic 400.</summary>
    private sealed class RefusingWritesHandler : HttpMessageHandler
    {
        public int Puts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string b, HttpStatusCode code = HttpStatusCode.OK) =>
                new(code) { Content = new StringContent(b, Encoding.UTF8, "application/json") };

            if (path.Contains("/oauth/v2/token", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json("{\"access_token\":\"stub-token\"}"));
            if (request.Method == HttpMethod.Put)
            {
                Puts++;
                return Task.FromResult(Json(
                    "{\"status_code\":\"400\",\"message\":\"An unexpected error occurred. Please check your input parameters.\"}",
                    HttpStatusCode.BadRequest));
            }
            if (path.Contains("/sponsors/", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json("{\"sponsor\":{\"company_name\":\"Company Ten\",\"website_url\":\"\",\"description\":\"\"}}"));
            if (path.Contains("/exhibitors/", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json("{\"exhibitor\":{\"company_name\":\"Company Ten\",\"website_url\":\"\",\"company_overview\":\"\",\"company_social_pages\":{}}}"));
            return Task.FromResult(Json("{\"sponsors\":[],\"exhibitors\":[]}"));
        }
    }

    /// <summary>
    /// 🔴 §1225 — "Exhibitor · Pinksky · Company Social Pages → LinkedIn" was mailed as hand-entry
    /// because Zoho answered the PUT with its generic 400. Operator: <i>"this email is wrong, as we
    /// have api, remove this email"</i>. A refused push of an API-writable field retries; it is never
    /// work for a human.
    /// </summary>
    [Fact]
    public async Task A_refused_push_of_writable_fields_is_retried_not_mailed_as_hand_entry()
    {
        using var db = ScenarioFixture.NewDb();
        var handler = new RefusingWritesHandler();
        var eventId = await SeedAsync(db, LinkedBoothCompany());

        var result = await NewSync(db, handler).SyncAsync(eventId, "10", "Company Ten", notifyZohoChange: false);

        Assert.True(handler.Puts > 0);                               // it did try
        Assert.Empty(result.ManualLines ?? Array.Empty<string>());   // and asked nobody to type it in
        Assert.Empty(result.ExhibitorFields ?? Array.Empty<string>());
        Assert.Empty(result.SponsorFields ?? Array.Empty<string>());
    }

    /// <summary>
    /// 🔒 The other half: an unread run must not CLEAR the §1175 ledger either. That reset is what
    /// kept the announcement coming back after every good read.
    /// </summary>
    [Fact]
    public async Task An_unreadable_record_does_not_reset_the_announcement_ledger()
    {
        using var db = ScenarioFixture.NewDb();
        var info = LinkedBoothCompany();
        for (var i = 0; i < ZohoPushLedger.ReportAfterAttempts; i++)
            ZohoPushLedger.RecordSent(info, ZohoPushLedger.SponsorKey("website_url"), info.WebsiteUrl);
        var eventId = await SeedAsync(db, info);

        await NewSync(db, new RefusingReadsHandler()).SyncAsync(eventId, "10", "Company Ten", notifyZohoChange: false);

        var after = await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "10");
        Assert.Contains(ZohoPushLedger.Stuck(after), e => e.Field == ZohoPushLedger.SponsorKey("website_url"));
    }
}
