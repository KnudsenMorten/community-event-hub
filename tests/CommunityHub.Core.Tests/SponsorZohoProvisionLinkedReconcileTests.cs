using System.Net;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
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

    private static SponsorZohoSyncService NewSyncService(CommunityHubDbContext db)
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
        var cmOptions = new CompanyManagerOptions { Enabled = false };
        return new SponsorZohoSyncService(
            new ZohoClient(http, zohoOptions, NullLogger<ZohoClient>.Instance),
            db, zohoOptions, new CompanyManagerClient(new HttpClient(handler), cmOptions), cmOptions,
            NullLogger<SponsorZohoSyncService>.Instance);
    }

    /// <summary>
    /// 🔴 §792.5 — <b>the batched hand-entry mail must not repeat every pass.</b>
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: *"stamp on the batched path too"*. The per-company path stamped
    /// inline, but both BULK paths pass <c>notifyZohoChange: false</c> and mail once per run — so
    /// nothing was stamped, and the identical list would go out every 10 minutes for ever. A field
    /// he has not yet typed into Backstage still does not match on the next pass, so "report every
    /// mismatch" never converges on its own.</para>
    ///
    /// <para>🔒 Only the companies actually IN the mail are stamped. Stamping the edition would
    /// silence companies he was never told about — the original <c>ZohoSocialPushedHash</c> failure
    /// (§784.13), where a stamp recorded intent rather than delivery.</para>
    /// </remarks>
    [Fact]
    public async Task Stamping_marks_only_the_reported_companies_and_stops_the_mail_repeating()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        db.SponsorInfos.AddRange(
            new SponsorInfo
            {
                EventId = eventId, SponsorCompanyId = "reported",
                ZohoSponsorId = "ZSP-R", LinkedInUrl = "https://linkedin.com/company/reported",
            },
            new SponsorInfo
            {
                EventId = eventId, SponsorCompanyId = "untouched",
                ZohoSponsorId = "ZSP-U", LinkedInUrl = "https://linkedin.com/company/untouched",
            });
        await db.SaveChangesAsync();

        var sync = NewSyncService(db);

        // Both start unstamped — the state after the operator's catch-up flush.
        Assert.All(await db.SponsorInfos.ToListAsync(),
            s => Assert.Null(s.ZohoSponsorProfilePushedHash));

        var stamped = await sync.StampManualReportAsync(eventId, new[] { "reported" });

        Assert.Equal(1, stamped);

        var reported = await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "reported");
        var untouched = await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "untouched");

        Assert.NotNull(reported.ZohoSponsorProfilePushedHash);   // told him ⇒ do not tell him again
        Assert.Null(untouched.ZohoSponsorProfilePushedHash);     // never told ⇒ must still be told

        // Idempotent: stamping the same company again changes nothing, so an overlapping run
        // (provision + bulk re-sync both firing) cannot corrupt the record of what he was sent.
        var before = reported.ZohoSponsorProfilePushedHash;
        Assert.Equal(1, await sync.StampManualReportAsync(eventId, new[] { "reported" }));
        Assert.Equal(before,
            (await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "reported"))
                .ZohoSponsorProfilePushedHash);

        // And a company with nothing to report is never stamped — there is no claim to record.
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = eventId, SponsorCompanyId = "empty", ZohoSponsorId = "ZSP-E",
        });
        await db.SaveChangesAsync();
        Assert.Equal(0, await sync.StampManualReportAsync(eventId, new[] { "empty" }));
        Assert.Null((await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "empty"))
            .ZohoSponsorProfilePushedHash);
    }

    /// <summary>
    /// 🔴 §792.7 — a new booth VIDEO or COLLATERAL file must re-open the report.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: *"i must get booth collateral and videos by email also"* … *"if chg
    /// happens"*. Zoho has NO API for either (every candidate endpoint 404s), so they can only ever
    /// be hand-work — and they live on <c>SponsorBoothMaterials</c>, NOT on <c>SponsorInfo</c>.</para>
    ///
    /// <para>⚠️ That is the trap this pins: the stamp is computed from the company's reportable
    /// values, so if the materials were left out of it, a sponsor adding a video would change
    /// nothing the stamp can see. The mail would stay silent for ever about the one thing he asked
    /// to be told about.</para>
    /// </remarks>
    [Fact]
    public async Task Adding_a_booth_video_or_collateral_file_re_opens_the_report()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = eventId, SponsorCompanyId = "42", ZohoSponsorId = "ZSP-42",
            WebsiteUrl = "https://example.test",
        });
        await db.SaveChangesAsync();

        var sync = NewSyncService(db);
        await sync.StampManualReportAsync(eventId, new[] { "42" });
        var afterFirstReport = (await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "42"))
            .ZohoSponsorProfilePushedHash;
        Assert.NotNull(afterFirstReport);

        // The sponsor uploads a video. Nothing on SponsorInfo changed.
        db.SponsorBoothMaterials.Add(new SponsorBoothMaterial
        {
            EventId = eventId, SponsorCompanyId = "42",
            Kind = BoothMaterialKind.Video, Url = "https://youtu.be/example",
        });
        await db.SaveChangesAsync();

        await sync.StampManualReportAsync(eventId, new[] { "42" });
        var afterVideo = (await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "42"))
            .ZohoSponsorProfilePushedHash;

        // A different stamp ⇒ the next pass sees "changed" ⇒ he is told about the video.
        Assert.NotEqual(afterFirstReport, afterVideo);

        // ...and a collateral file moves it again.
        db.SponsorBoothMaterials.Add(new SponsorBoothMaterial
        {
            EventId = eventId, SponsorCompanyId = "42",
            Kind = BoothMaterialKind.Collateral,
            Url = "https://sp.example/brochure.pdf", FileName = "brochure.pdf",
        });
        await db.SaveChangesAsync();

        await sync.StampManualReportAsync(eventId, new[] { "42" });
        Assert.NotEqual(afterVideo,
            (await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "42"))
                .ZohoSponsorProfilePushedHash);

        // 🔒 But an unchanged set must hash the SAME, or the mail fires on every pass — the
        // 70-mail night with a different trigger.
        var stable = (await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "42"))
            .ZohoSponsorProfilePushedHash;
        await sync.StampManualReportAsync(eventId, new[] { "42" });
        Assert.Equal(stable,
            (await db.SponsorInfos.SingleAsync(s => s.SponsorCompanyId == "42"))
                .ZohoSponsorProfilePushedHash);
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

    /// <summary>
    /// 🔴 §791.3 — <b>THE REVERSAL.</b> This test asserted the OPPOSITE until 2026-08-04: that a
    /// blank-in-Zoho LinkedIn was PUSHED into <c>company_social_pages.linkedin</c>.
    /// </summary>
    /// <remarks>
    /// <para>Four controlled PUTs against the live PROD API proved the field is <b>accepted, echoed
    /// in the response body, and silently discarded</b> — with any platform key, with or without
    /// companion fields, and even on a record whose social pages are already populated. It behaves
    /// read-only over v3.</para>
    ///
    /// <para>⚠️ <b>The test passing was never evidence the value arrived.</b> It asserted the
    /// REQUEST BODY, which was correct all along — that is exactly why this defect survived three
    /// sessions and a green suite. What it can honestly pin is that we no longer send a field Zoho
    /// throws away, and that the operator is told to set it by hand instead.</para>
    /// </remarks>
    [Fact]
    public async Task Blank_linkedin_is_NOT_pushed_and_is_reported_as_a_manual_Backstage_action()
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

        // 🔴 §792 PLAN B — NO UPDATE IS SENT TO ZOHO AT ALL, for either object.
        //
        // Operator 2026-08-04: *"Any api UPDATES related to sponsors and exhibitors must be sent to
        // info@expertslive.dk as mail"* … *"we will not spend more time on api UPDATES in zoho
        // anymore until they fix it"*. This asserts the ABSENCE of the call, which is the whole
        // change — asserting the request body would keep passing if a PUT crept back in.
        Assert.DoesNotContain(
            handler.Calls,
            c => c.Method == HttpMethod.Put
                 && c.Path.EndsWith("/exhibitors/ZEX-10", StringComparison.Ordinal));

        Assert.DoesNotContain(
            handler.Calls,
            c => c.Method == HttpMethod.Put
                 && c.Path.EndsWith("/sponsors/ZSP-10", StringComparison.Ordinal));

        // 🔒 And nothing anywhere may carry company_social_pages or a contact block on an UPDATE —
        // §791.3 (Zoho discards social) and §791.5 (his instruction: no contact details on update,
        // because Zoho hard-caps contact e-mail updates at 3 and a no-op resend burns one).
        foreach (var put in handler.Calls.Where(c => c.Method == HttpMethod.Put && c.Body is not null))
        {
            using var doc = JsonDocument.Parse(put.Body!);
            Assert.False(doc.RootElement.TryGetProperty("company_social_pages", out _));
            Assert.False(doc.RootElement.TryGetProperty("contact", out _));
        }
    }
}
