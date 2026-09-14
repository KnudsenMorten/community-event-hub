using System.Net;
using System.Text;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1219 — the reconcile's automatic public-name fixes are RECEIPTS, not to-dos.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-14, on a mail titled "2 need(s) your attention" whose first line said the
/// empty public name had been set to "Egiss" (legal form A/S removed): <i>"it should be in the logic
/// already and did fix it, so mail is wrong"</i>. The fill worked; the subject counted it as work.</para>
///
/// <para>FAKE names only.</para>
/// </remarks>
public sealed class ErpReconcilePublicNameReceiptTests
{
    private sealed class CmHandler : HttpMessageHandler
    {
        private readonly string _legal;
        private readonly string _public;
        public List<string> PutBodies { get; } = new();

        public CmHandler(string legal, string @public) { _legal = legal; _public = @public; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string b) =>
                new(HttpStatusCode.OK) { Content = new StringContent(b, Encoding.UTF8, "application/json") };

            if (req.Method == HttpMethod.Put)
            {
                PutBodies.Add(await req.Content!.ReadAsStringAsync(ct));
                return Json("{}");
            }

            if (req.Method == HttpMethod.Get && path.EndsWith("/companies"))
                return Json("[{\"id\":1,\"erp_customer_number\":\"100\",\"name\":\"x\","
                    + "\"default_signer_id\":11,\"event_coordination_default_contact_id\":11}]");

            if (req.Method == HttpMethod.Get && path.Contains("/companies/") && !path.EndsWith("/users"))
                return Json($"{{\"id\":1,\"name\":\"{_legal}\",\"company_name_public\":\"{_public}\","
                    + "\"erp_customer_number\":\"100\",\"default_signer_id\":11,\"event_coordination_default_contact_id\":11}");

            if (req.Method == HttpMethod.Get && path.Contains("/companies/") && path.EndsWith("/users"))
                return Json("[{\"user_id\":11,\"email\":\"p100@x.test\",\"full_name\":\"Person 100\",\"display_name\":\"Person 100\"}]");

            return Json("{}");
        }
    }

    private sealed class StubErp : IEconomicContactAdminClient
    {
        private readonly string _name;
        public StubErp(string name) => _name = name;
        public bool CanWrite => true;

        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(new[] { new EconomicCustomerRow(100, _name, null) });

        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EconomicContactRow>>(new[]
            {
                new EconomicContactRow(1, "Person 100", "p100@x.test", "+45", "Role:1,2"),
            });

        public Task<int> CreateContactAsync(int c, EconomicContactInput i, CancellationToken ct = default) => Task.FromResult(1);
        public Task UpdateContactAsync(int c, int n, EconomicContactInput i, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteContactAsync(int c, int n, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingEmail : IEmailSender
    {
        public List<string> Subjects { get; } = new();
        public List<string> Bodies { get; } = new();

        public Task SendAsync(string to, string s, string h, CancellationToken ct = default)
        { Subjects.Add(s); Bodies.Add(h); return Task.CompletedTask; }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string i, string f, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, s, h, ct);
    }

    private static ErpWebshopContactSyncService Sut(CmHandler handler, string erpName, IEmailSender email)
    {
        var cm = new CompanyManagerClient(new HttpClient(handler), new CompanyManagerOptions
        {
            Enabled = true,
            BaseUrl = "https://cm.test/wp-json/company-manager/v1",
            Username = "u",
            Password = "p",
        });
        return new ErpWebshopContactSyncService(
            new EconomicContactAdminService(new StubErp(erpName)), cm,
            new CompanyManagerOptions { Enabled = true }, email,
            NullLogger<ErpWebshopContactSyncService>.Instance);
    }

    [Fact]
    public async Task An_automatic_public_name_fill_is_reported_but_does_NOT_count_as_attention()
    {
        // Baseline: the same company with its public name ALREADY clean. Whatever else the fake
        // produces (billing fields and so on) is the same in both runs, so only the fill differs.
        var cleanEmail = new CapturingEmail();
        await Sut(new CmHandler(legal: "Fakeco A/S", @public: "Fakeco"), "Fakeco A/S", cleanEmail).SyncAsync();

        var handler = new CmHandler(legal: "Fakeco A/S", @public: "");
        var email = new CapturingEmail();
        await Sut(handler, "Fakeco A/S", email).SyncAsync();

        Assert.Contains(handler.PutBodies, b => b.Contains("\"company_name_public\":\"Fakeco\""));

        // Still told — a quiet fix must not become a silent one.
        Assert.Contains("PUBLIC NAME set automatically", Assert.Single(email.Bodies), StringComparison.Ordinal);

        // …but the attention count is exactly what it is without the fill.
        Assert.Equal(AttentionCount(cleanEmail.Subjects.SingleOrDefault()), AttentionCount(email.Subjects.Single()));
    }

    /// <summary>The N in "N need(s) your attention"; 0 for a "nothing to do" subject or no mail.</summary>
    private static int AttentionCount(string? subject)
    {
        if (subject is null) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(subject, @"(\d+) need\(s\) your attention");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
}
