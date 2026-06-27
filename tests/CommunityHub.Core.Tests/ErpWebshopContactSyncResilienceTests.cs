using System.Net;
using System.Text;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Verifies the per-company graceful catch in <see cref="ErpWebshopContactSyncService"/>
/// (REQUIREMENTS §138, the 2026-06-27 incident): when ONE company's Company Manager
/// call fails hard (a 503 that survives the HttpClient retry), the reconcile logs a
/// warning + records an alert-note and CONTINUES to the next company, instead of
/// throwing out of the whole job and abandoning every remaining company.
/// </summary>
public sealed class ErpWebshopContactSyncResilienceTests
{
    /// <summary>Routes Company Manager calls by HTTP method + absolute path; company id 1's
    /// user list always returns 503 so its per-company branch fails.</summary>
    private sealed class CmHandler : HttpMessageHandler
    {
        public int CreateUserCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(HttpStatusCode code, string body) =>
                new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            // GET /companies — the slim list keyed by erp_customer_number.
            if (req.Method == HttpMethod.Get && path.EndsWith("/companies"))
                return Task.FromResult(Json(HttpStatusCode.OK,
                    "[{\"id\":1,\"erp_customer_number\":\"100\",\"name\":\"Alpha\",\"default_signer_id\":0,\"event_coordination_default_contact_id\":0},"
                    + "{\"id\":2,\"erp_customer_number\":\"200\",\"name\":\"Beta\",\"default_signer_id\":0,\"event_coordination_default_contact_id\":0}]"));

            // GET /companies/{id}/users — company 1 is the failing one.
            if (req.Method == HttpMethod.Get && path.EndsWith("/users") && path.Contains("/companies/"))
            {
                if (path.Contains("/companies/1/"))
                    return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "upstream down"));
                return Task.FromResult(Json(HttpStatusCode.OK, "[]")); // company 2: no users yet
            }

            // POST /users — create a webshop user (company 2's contact).
            if (req.Method == HttpMethod.Post && path.EndsWith("/users"))
            {
                CreateUserCalls++;
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"user_id\":999}"));
            }

            // PUT /companies/{id} — set defaults.
            if (req.Method == HttpMethod.Put && path.Contains("/companies/"))
                return Task.FromResult(Json(HttpStatusCode.OK, "{}"));

            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        }
    }

    /// <summary>Two sponsor customers (#100 ↔ company 1, #200 ↔ company 2), each with one
    /// signer/coordinator contact.</summary>
    private sealed class StubErp : IEconomicContactAdminClient
    {
        public bool CanWrite => true;
        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(new[]
            {
                new EconomicCustomerRow(100, "Alpha", "a@alpha.test"),
                new EconomicCustomerRow(200, "Beta", "b@beta.test"),
            });

        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(new[]
            {
                new EconomicContactRow(1, customerNumber == 100 ? "Alpha Person" : "Beta Person",
                    customerNumber == 100 ? "a@alpha.test" : "b@beta.test", "+45", "Role:1,2"),
            });

        public Task<int> CreateContactAsync(int c, EconomicContactInput i, CancellationToken ct = default) => Task.FromResult(1);
        public Task UpdateContactAsync(int c, int n, EconomicContactInput i, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteContactAsync(int c, int n, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopEmail : IEmailSender
    {
        public int Sends { get; private set; }
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default) { Sends++; return Task.CompletedTask; }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, s, h, ct);
    }

    [Fact]
    public async Task One_company_failing_does_not_abort_the_others()
    {
        var cmHandler = new CmHandler();
        var cm = new CompanyManagerClient(new HttpClient(cmHandler), new CompanyManagerOptions
        {
            Enabled = true,
            BaseUrl = "https://cm.test/wp-json/company-manager/v1",
            Username = "u",
            Password = "p",
        });
        var erp = new EconomicContactAdminService(new StubErp());
        var email = new NoopEmail();
        var svc = new ErpWebshopContactSyncService(
            erp, cm, new CompanyManagerOptions { Enabled = true }, email,
            NullLogger<ErpWebshopContactSyncService>.Instance);

        // Must NOT throw even though company 1's GetCompanyUsersAsync returns 503.
        var result = await svc.SyncAsync();

        Assert.True(result.Enabled);
        Assert.Equal(2, result.Customers);

        // Company 2 still reconciled: its contact was created.
        Assert.Equal(1, result.UsersCreated);
        Assert.Equal(1, cmHandler.CreateUserCalls);

        // Company 1's failure was recorded as an alert-note (and CONTINUED).
        Assert.Contains(result.AlertNotes, n =>
            n.Contains("Alpha") && n.Contains("failed", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Alerts >= 1);
    }
}
