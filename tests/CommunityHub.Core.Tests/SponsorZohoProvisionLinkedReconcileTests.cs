using System.Net;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §41b: the provision job (WooCommercePullJob → ProvisionAsync) must run
/// the blank-only Zoho←CEH social/web reconcile for ALREADY-LINKED sponsors/exhibitors,
/// not only at create-time. Before the fix, an existing linked EXHIBITOR (booth company,
/// e.g. companies 10 &amp; 19) whose LinkedIn was blank in Zoho only got it pushed by a
/// manual sponsor save or the SponsorAdmin "Migrate+Resync" button — the 15-min job never
/// filled it. (LinkedIn/Twitter live on the Zoho EXHIBITOR record; the sponsor record has
/// no social fields, which is why a booth company is the case that proves this.) This test
/// proves the job now PUTs a blank-in-Zoho LinkedIn for a linked exhibitor, and that it
/// does NOT re-send the contact email when it is unchanged (§41a 3× email-update cap).
/// Fully offline: a stub HttpMessageHandler answers Zoho — NO live Zoho calls.
/// </summary>
public class SponsorZohoProvisionLinkedReconcileTests
{
    private sealed record ZohoCall(HttpMethod Method, string Path, string? Body);

    /// <summary>
    /// Offline Zoho: token POST → fake token; list GETs → empty pages; GET-by-id → a
    /// sponsor whose LinkedIn (and everything social) is BLANK in Zoho; PUT → 200 OK.
    /// Every call is recorded so the test can assert exactly what was sent.
    /// </summary>
    private sealed class StubZohoHandler : HttpMessageHandler
    {
        public List<ZohoCall> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string? body = request.Content is null ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new ZohoCall(request.Method, path, body));

            // OAuth token refresh.
            if (path.Contains("/oauth/v2/token", StringComparison.OrdinalIgnoreCase))
                return Json("{\"access_token\":\"stub-token\"}");

            // PUT update (sponsor/exhibitor) — accept.
            if (request.Method == HttpMethod.Put)
                return Json("{\"ok\":true}");

            // GET a single sponsor by id: blank social/web/description → safe to fill.
            if (request.Method == HttpMethod.Get
                && path.Contains("/sponsors/", StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    "{\"sponsor\":{\"website_url\":\"\",\"description\":\"\"," +
                    "\"company_social_pages\":{}}}");
            }

            // GET a single EXHIBITOR by id: blank social/web/overview → safe to fill.
            // (LinkedIn/Twitter live on the exhibitor record, not the sponsor.)
            if (request.Method == HttpMethod.Get
                && path.Contains("/exhibitors/", StringComparison.OrdinalIgnoreCase))
            {
                return Json(
                    "{\"exhibitor\":{\"website_url\":\"\",\"company_overview\":\"\"," +
                    "\"company_social_pages\":{}}}");
            }

            // GET list endpoints. The linked sponsor/exhibitor MUST appear here so the
            // provision self-heal ("cached id not in Zoho ⇒ stale ⇒ re-create") does NOT
            // wipe the cached ids — i.e. the company stays ALREADY-LINKED, which is the
            // whole point of this test. Each list response carries only its own array key;
            // PageV3Async reads by key so extra keys are harmless.
            if (path.EndsWith("/sponsors", StringComparison.Ordinal))
                return Json("{\"sponsors\":[{\"id\":\"ZSP-10\",\"company_name\":\"Company Ten\"}]}");
            if (path.EndsWith("/exhibitors", StringComparison.Ordinal))
                return Json("{\"exhibitors\":[{\"id\":\"ZEX-10\",\"company_name\":\"Company Ten\"}]}");
            return Json("{\"sponsorship_types\":[],\"booths\":[]}");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    private static (SponsorZohoProvisionService Provision, StubZohoHandler Handler) NewService(
        CommunityHubDbContext db)
    {
        var handler = new StubZohoHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://stub.local/") };
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
        var zoho = new ZohoClient(http, zohoOptions, NullLogger<ZohoClient>.Instance);

        // Company Manager OFF → ProvisionAsync skips the webshop/coordinator calls and the
        // reconcile's webshop round-trip; the only network is the stub Zoho above.
        var cmOptions = new CompanyManagerOptions { Enabled = false };
        var cm = new CompanyManagerClient(new HttpClient(handler), cmOptions);

        var sync = new SponsorZohoSyncService(
            zoho, db, zohoOptions, cm, cmOptions,
            NullLogger<SponsorZohoSyncService>.Instance);

        var provision = new SponsorZohoProvisionService(
            zoho, db, zohoOptions, cm, cmOptions,
            exhibitorApi: null!, new EventEditionConfigLoader(), new EventConfigOptions(),
            sync, NullLogger<SponsorZohoProvisionService>.Instance);

        return (provision, handler);
    }

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "Test Community",
            DisplayName = "Test Community 2027",
            Code = "TC27",
            IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    [Fact]
    public async Task Linked_exhibitor_with_blank_in_zoho_linkedin_gets_it_pushed_by_provision()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        // An ALREADY-LINKED exhibitor (booth company, like 10 & 19): has BOTH a
        // ZohoSponsorId and a ZohoExhibitorId, so the create/link block is a no-op and the
        // §41b reconcile is the only thing that runs. Its CEH record carries a LinkedIn
        // URL; Zoho holds it BLANK (stub GET-by-id). The email was already pushed before
        // (ZohoContactEmail == EventCoordinatorEmail) so it must NOT be re-sent. BoothLabel
        // left null so the booth-assign PUT doesn't fire — keeps the test on the reconcile.
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = eventId,
            SponsorCompanyId = "10",
            SponsorPackage = SponsorPackage.Gold,     // booth → also a Zoho exhibitor
            ZohoSponsorId = "ZSP-10",
            ZohoExhibitorId = "ZEX-10",
            LinkedInUrl = "https://linkedin.com/company/ten",
            EventCoordinatorEmail = "coord@example.com",
            ZohoContactEmail = "coord@example.com",   // unchanged → email must not re-send
        });
        await db.SaveChangesAsync();

        var (provision, handler) = NewService(db);
        var result = await provision.ProvisionAsync(eventId);

        Assert.True(result.Enabled);

        // A PUT to the linked EXHIBITOR record must have been sent (and exactly once)…
        var put = Assert.Single(
            handler.Calls,
            c => c.Method == HttpMethod.Put && c.Path.EndsWith("/exhibitors/ZEX-10", StringComparison.Ordinal));

        using var doc = JsonDocument.Parse(put.Body!);
        var root = doc.RootElement;

        // …carrying the LinkedIn URL into company_social_pages.linkedin (the §41b fill).
        Assert.True(root.TryGetProperty("company_social_pages", out var social));
        Assert.Equal(
            "https://linkedin.com/company/ten",
            social.GetProperty("linkedin").GetString());

        // EMAIL SAFETY (§41a): the contact email was unchanged, so the PUT must NOT carry a
        // contact.email (a no-op resend would burn one of Zoho's 3 allowed email updates).
        if (root.TryGetProperty("contact", out var contact))
            Assert.False(contact.TryGetProperty("email", out _),
                "Unchanged contact email must NOT be re-sent to Zoho.");
    }
}
