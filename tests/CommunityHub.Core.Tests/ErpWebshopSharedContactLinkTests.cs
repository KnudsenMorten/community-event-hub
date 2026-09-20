using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1235b — THE ERP→WEBSHOP SYNC MUST LINK A SHARED CONTACT, NOT GIVE UP ON THEM.
///
/// <para>A sponsor group's divisions are separate ERP accounts carrying the SAME contacts (operator
/// 2026-09-16: <i>"in my erp they will exist with 2 contacts under each account. contacts are the
/// same"</i>). The webshop refuses to create a user whose address already exists, so the second
/// division's create was rejected and the reconcile reported <i>"NOT LINKED … a person can belong to
/// only ONE company"</i> — a statement about this sync, not about the webshop, which has had a
/// link-user-to-company endpoint all along that NOTHING called.</para>
/// </summary>
public sealed class ErpWebshopSharedContactLinkTests
{
    /// <summary>
    /// Two companies (1 ↔ ERP #100, 2 ↔ ERP #200) whose ERP accounts list the SAME contact.
    /// Company 1 already has them as a webshop user; POST /users is refused for company 2, the way a
    /// webshop refuses a duplicate address.
    /// </summary>
    private sealed class CmHandler : HttpMessageHandler
    {
        public int CreateUserCalls { get; private set; }
        public List<int> LinkedToCompanies { get; } = new();
        public List<int> LinkedUserIds { get; } = new();

        /// <summary>§1235c — what `GET /users?search=` answers, when the test wants a webshop hit.</summary>
        public (string Email, int UserId)? UserSearchHit { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            static HttpResponseMessage Json(HttpStatusCode code, string body) =>
                new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            if (req.Method == HttpMethod.Get && path.EndsWith("/companies"))
                return Json(HttpStatusCode.OK,
                    "[{\"id\":1,\"erp_customer_number\":\"100\",\"name\":\"Group DK\",\"default_signer_id\":0,\"event_coordination_default_contact_id\":0},"
                    + "{\"id\":2,\"erp_customer_number\":\"200\",\"name\":\"Group SE\",\"default_signer_id\":0,\"event_coordination_default_contact_id\":0}]");

            // §1235c — GET /users?search=<email>. Loose by nature in the real plugin; the client
            // filters to an exact e-mail match, so the stub answers with the whole row.
            if (req.Method == HttpMethod.Get && path.EndsWith("/users") && !path.Contains("/companies/"))
            {
                var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["search"];
                return UserSearchHit is { } hit
                       && string.Equals(hit.Email, q, StringComparison.OrdinalIgnoreCase)
                    ? Json(HttpStatusCode.OK,
                        $"[{{\"id\":{hit.UserId},\"email\":\"{hit.Email}\"}}]")
                    : Json(HttpStatusCode.OK, "[]");
            }

            // GET /companies/{id}/users — company 1 already holds the shared person (user 4242).
            if (req.Method == HttpMethod.Get && path.EndsWith("/users") && path.Contains("/companies/"))
                return path.Contains("/companies/1/")
                    ? Json(HttpStatusCode.OK,
                        "[{\"user_id\":4242,\"user_email\":\"cmo@group.test\",\"full_name\":\"Global CMO\"}]")
                    : Json(HttpStatusCode.OK, "[]");

            // POST /companies/{id}/users — the LINK call this feature depends on.
            if (req.Method == HttpMethod.Post && path.EndsWith("/users") && path.Contains("/companies/"))
            {
                var companyId = int.Parse(path.Split("/companies/")[1].Split('/')[0]);
                LinkedToCompanies.Add(companyId);
                var body = await req.Content!.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                LinkedUserIds.Add(doc.RootElement.GetProperty("user_id").GetInt32());
                return Json(HttpStatusCode.OK, "{}");
            }

            // POST /users — the webshop REFUSES a duplicate address (the real 409 shape).
            if (req.Method == HttpMethod.Post && path.EndsWith("/users"))
            {
                CreateUserCalls++;
                return Json(HttpStatusCode.Conflict, "{\"code\":\"existing_user_login\"}");
            }

            if (req.Method == HttpMethod.Put && path.Contains("/companies/"))
                return Json(HttpStatusCode.OK, "{}");

            return Json(HttpStatusCode.OK, "{}");
        }
    }

    /// <summary>Both ERP accounts carry the SAME contact — the group's global CMO.</summary>
    private sealed class StubErp : IEconomicContactAdminClient
    {
        public bool CanWrite => true;

        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(new[]
            {
                new EconomicCustomerRow(100, "Group DK", "dk@group.test"),
                new EconomicCustomerRow(200, "Group SE", "se@group.test"),
            });

        // 🔑 The SAME e-mail under both accounts, and both accounts number them 1 — per-customer
        // sequences, which is why a contact number is only meaningful inside its account.
        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(new[]
            {
                new EconomicContactRow(1, "Global CMO", "cmo@group.test", "+45", "Role:1,2"),
            });

        public Task<int> CreateContactAsync(int c, EconomicContactInput i, CancellationToken ct = default) => Task.FromResult(1);
        public Task UpdateContactAsync(int c, int n, EconomicContactInput i, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteContactAsync(int c, int n, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopEmail : IEmailSender
    {
        public List<string> Bodies { get; } = new();
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default) { Bodies.Add(h); return Task.CompletedTask; }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, s, h, ct);
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"erplink-{Guid.NewGuid():N}").Options);

    [Fact]
    public async Task A_shared_contact_already_in_the_webshop_is_LINKED_to_the_second_company()
    {
        using var db = NewDb();
        var ev = new Event { CommunityName = "C", DisplayName = "D", Code = "ELDK27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        // The hub already knows this person and the webshop user id behind them — written by the
        // contact sync when company 1 was mirrored. That id is what the link is made by.
        db.Participants.Add(new Participant
        {
            EventId = ev.Id, Email = "cmo@group.test", FullName = "Global CMO",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = "1", CmUserId = 4242, IsActive = true,
        });
        await db.SaveChangesAsync();

        var handler = new CmHandler();
        var cmOptions = new CompanyManagerOptions
        {
            Enabled = true, BaseUrl = "https://cm.test/wp-json/company-manager/v1",
            Username = "u", Password = "p",
        };
        var email = new NoopEmail();
        var svc = new ErpWebshopContactSyncService(
            new EconomicContactAdminService(new StubErp()),
            new CompanyManagerClient(new HttpClient(handler), cmOptions),
            cmOptions, email, NullLogger<ErpWebshopContactSyncService>.Instance, db: db);

        var result = await svc.SyncAsync();

        Assert.True(result.Enabled);
        // The create was attempted for company 2 and refused…
        Assert.True(handler.CreateUserCalls > 0);
        // …so the existing user was LINKED to that company instead.
        Assert.Contains(2, handler.LinkedToCompanies);
        Assert.Contains(4242, handler.LinkedUserIds);

        // 🔒 And the report says LINKED, not the old "a person can belong to only ONE company".
        var report = string.Join("\n", email.Bodies);
        if (report.Length > 0)
        {
            Assert.DoesNotContain("belong to only ONE company", report, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 🔴 §1235c — THE WEBSHOP IS ASKED WHEN CEH HOLDS NO ID. A brand-new company's contacts are
    /// precisely the ones the hub has never synced, so relying on `Participant.CmUserId` alone left
    /// them unlinked (measured live: Truesec A/S, company 108, created and empty).
    /// </summary>
    [Fact]
    public async Task A_contact_unknown_to_the_hub_is_found_in_the_webshop_and_linked()
    {
        using var db = NewDb();   // NO participants at all — the hub knows nobody.
        var ev = new Event { CommunityName = "C", DisplayName = "D", Code = "ELDK27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var handler = new CmHandler { UserSearchHit = ("cmo@group.test", 4242) };
        var cmOptions = new CompanyManagerOptions
        {
            Enabled = true, BaseUrl = "https://cm.test/wp-json/company-manager/v1",
            Username = "u", Password = "p",
        };
        var svc = new ErpWebshopContactSyncService(
            new EconomicContactAdminService(new StubErp()),
            new CompanyManagerClient(new HttpClient(handler), cmOptions),
            cmOptions, new NoopEmail(), NullLogger<ErpWebshopContactSyncService>.Instance, db: db);

        await svc.SyncAsync();

        Assert.Contains(2, handler.LinkedToCompanies);
        Assert.Contains(4242, handler.LinkedUserIds);
    }

    /// <summary>
    /// ⚠️ An address neither the hub NOR the webshop knows has nothing to link by — that case must
    /// still report NOT LINKED rather than claim success.
    /// </summary>
    [Fact]
    public async Task An_unknown_address_still_reports_not_linked()
    {
        using var db = NewDb();
        var ev = new Event { CommunityName = "C", DisplayName = "D", Code = "ELDK27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();   // no participant, so no CmUserId to link by

        var handler = new CmHandler();
        var cmOptions = new CompanyManagerOptions
        {
            Enabled = true, BaseUrl = "https://cm.test/wp-json/company-manager/v1",
            Username = "u", Password = "p",
        };
        var svc = new ErpWebshopContactSyncService(
            new EconomicContactAdminService(new StubErp()),
            new CompanyManagerClient(new HttpClient(handler), cmOptions),
            cmOptions, new NoopEmail(), NullLogger<ErpWebshopContactSyncService>.Instance, db: db);

        var result = await svc.SyncAsync();

        Assert.True(result.Enabled);
        Assert.Empty(handler.LinkedToCompanies);
        Assert.Contains(result.AlertNotes, n => n.Contains("NOT LINKED", StringComparison.Ordinal));
    }
}
