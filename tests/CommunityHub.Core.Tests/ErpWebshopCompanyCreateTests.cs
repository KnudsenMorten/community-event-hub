using System.Net;
using System.Text;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1238 — A SPONSOR THAT EXISTS IN THE ERP GETS A WEBSHOP COMPANY.
///
/// <para>Operator 2026-09-17, having created Truesec Sweden (e-conomic #341409358) and found nothing
/// in Company Manager: <i>"i dont see that it was created in CM module in wordpress yet even though it
/// was synced"</i> · <i>"if the company does not exist in CM, create it"</i> · <i>"using CM api"</i>.
/// The reconcile used to note *"no matching webshop company (erp_customer_number)"* and move on, for
/// ever.</para>
///
/// <para>🔑 ADOPT BEFORE CREATE: a same-named company with no ERP number is LINKED, never duplicated —
/// the webshop has no delete route (§505), so a duplicate there is expensive to unpick.</para>
/// </summary>
public sealed class ErpWebshopCompanyCreateTests
{
    private sealed class CmHandler : HttpMessageHandler
    {
        public CmHandler(string companiesJson) => _companiesJson = companiesJson;
        private readonly string _companiesJson;

        public List<string> CreatedCompanies { get; } = new();
        public List<(int Id, string Body)> Updates { get; } = new();
        public int NextId { get; set; } = 500;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            static HttpResponseMessage Json(HttpStatusCode code, string body) =>
                new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            if (req.Method == HttpMethod.Get && path.EndsWith("/companies"))
                return Json(HttpStatusCode.OK, _companiesJson);

            // POST /companies — the create under test.
            if (req.Method == HttpMethod.Post && path.EndsWith("/companies"))
            {
                CreatedCompanies.Add(await req.Content!.ReadAsStringAsync(ct));
                return Json(HttpStatusCode.OK, $"{{\"id\":{NextId}}}");
            }

            // PUT /companies/{id} — the adopt path.
            if (req.Method == HttpMethod.Put && path.Contains("/companies/"))
            {
                var id = int.Parse(path.Split("/companies/")[1].Split('/')[0]);
                Updates.Add((id, await req.Content!.ReadAsStringAsync(ct)));
                return Json(HttpStatusCode.OK, "{}");
            }

            if (req.Method == HttpMethod.Get && path.EndsWith("/users") && path.Contains("/companies/"))
                return Json(HttpStatusCode.OK, "[]");

            if (req.Method == HttpMethod.Post && path.EndsWith("/users"))
                return Json(HttpStatusCode.OK, "{\"user_id\":9001}");

            return Json(HttpStatusCode.OK, "{}");
        }
    }

    /// <summary>One sponsor-group customer that has no webshop company.</summary>
    private sealed class StubErp : IEconomicContactAdminClient
    {
        public bool CanWrite => true;

        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(new[]
            {
                new EconomicCustomerRow(341409358, "Truesec Sweden AB", "se@truesec.test"),
            });

        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(new[]
            {
                new EconomicContactRow(1, "Nathalie Taub", "nathalie.taub@truesec.test", "+46", "Role:1,2"),
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

    private static ErpWebshopContactSyncService NewSvc(CmHandler handler)
    {
        var opts = new CompanyManagerOptions
        {
            Enabled = true, BaseUrl = "https://cm.test/wp-json/company-manager/v1",
            Username = "u", Password = "p",
        };
        return new ErpWebshopContactSyncService(
            new EconomicContactAdminService(new StubErp()),
            new CompanyManagerClient(new HttpClient(handler), opts),
            opts, new NoopEmail(), NullLogger<ErpWebshopContactSyncService>.Instance);
    }

    [Fact]
    public async Task A_sponsor_with_no_webshop_company_gets_one_created()
    {
        // The webshop holds an unrelated company only.
        var handler = new CmHandler(
            "[{\"id\":37,\"erp_customer_number\":\"341409339\",\"name\":\"Truesec Group AB\","
            + "\"default_signer_id\":0,\"event_coordination_default_contact_id\":0}]");

        var result = await NewSvc(handler).SyncAsync();

        var created = Assert.Single(handler.CreatedCompanies);
        Assert.Contains("Truesec Sweden AB", created, StringComparison.Ordinal);
        Assert.Contains("341409358", created, StringComparison.Ordinal);
        Assert.Contains(result.AlertNotes, n => n.Contains("CREATED", StringComparison.Ordinal));
        // 🔒 …and the old dead-end note is gone.
        Assert.DoesNotContain(
            result.AlertNotes,
            n => n.Contains("no matching webshop company (erp_customer_number).", StringComparison.Ordinal));
    }

    /// <summary>
    /// 🔑 ADOPT: a same-named company with no ERP number is linked, not duplicated. A duplicate in a
    /// live webshop cannot be deleted through the API (§505).
    /// </summary>
    [Fact]
    public async Task A_same_named_company_without_an_erp_number_is_linked_not_duplicated()
    {
        var handler = new CmHandler(
            "[{\"id\":42,\"erp_customer_number\":\"\",\"name\":\"Truesec Sweden AB\","
            + "\"default_signer_id\":0,\"event_coordination_default_contact_id\":0}]");

        var result = await NewSvc(handler).SyncAsync();

        Assert.Empty(handler.CreatedCompanies);
        // The run makes other PUTs afterwards (defaults, billing, name), so assert the SPECIFIC one:
        // company 42 gained this ERP customer number.
        Assert.Contains(
            handler.Updates,
            u => u.Id == 42 && u.Body.Contains("erp_customer_number", StringComparison.Ordinal)
                            && u.Body.Contains("341409358", StringComparison.Ordinal));
        Assert.Contains(result.AlertNotes, n => n.Contains("LINKED", StringComparison.Ordinal));
    }

    /// <summary>⚠️ A company that ALREADY carries the ERP number is untouched — no create, no update.</summary>
    [Fact]
    public async Task An_already_linked_company_is_left_alone()
    {
        var handler = new CmHandler(
            "[{\"id\":50,\"erp_customer_number\":\"341409358\",\"name\":\"Truesec Sweden AB\","
            + "\"default_signer_id\":0,\"event_coordination_default_contact_id\":0}]");

        await NewSvc(handler).SyncAsync();

        Assert.Empty(handler.CreatedCompanies);
        Assert.DoesNotContain(handler.Updates, u => u.Body.Contains("erp_customer_number", StringComparison.Ordinal));
    }
}
