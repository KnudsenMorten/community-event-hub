using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1059 — THE ERP→WEBSHOP RECONCILE MUST NOT RUN ON A HOST THAT MAY NOT WRITE TO THE WEBSHOP.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11, for the SECOND time: <i>"DEV can NOT make changes in webshop !!!!"</i>
/// — <i>"this process will fail as it tries syncing from erp to webshop which is must not do"</i>.</para>
///
/// <para>🔑 <b>Nothing was ever written.</b> <c>CreateUserAsync</c> returns 0 at its §1041 guard,
/// before the HTTP request is built. What reached him was the REPORT: the caller cannot tell "the
/// guard refused" from "WordPress rejected the address", so a refusal rendered as <i>"could not be
/// added to the webshop … delete or re-link the old user in WordPress"</i> — telling him to hand-fix
/// a webshop that was perfectly correct.</para>
///
/// <para>🔴 <b>§1041b fixed exactly this — in the BILLING branch, and only there.</b> The
/// user-create branch is the same defect and produced the same e-mail a second time. Patching that
/// one message would have left a third caller to be found by a third e-mail; this job's EVERY
/// effect is a webshop write, so the fix is that it does not start.</para>
///
/// <para>⚠️ <b>Both halves below are load-bearing.</b> A gate that skipped everywhere would look
/// identical on DEV and pass a DEV-only test, while silently killing the reconcile in PROD — the
/// bigger failure, and a silent one. So the PROD-shaped case is pinned with the EXACT config read
/// out of Azure on 2026-08-11: <c>AllowExternalWrites = true</c> and <b>no</b> per-system override.</para>
/// </remarks>
public sealed class ErpWebshopReconcileSkippedWhenWebshopBlockedTests
{
    // ---- the environment ceiling, against the real guard and the real config shapes ----

    /// <summary>
    /// 🔒 The DEV config, verified on both hosts (<c>fn</c> + <c>web</c>) 2026-08-11.
    /// ⚠️ Note <c>Erp = true</c> is asserted alongside: writing to e-conomic from DEV is ACCEPTED
    /// (§1044, reconfirmed <i>"i accept it can write to ERP in dev"</i>). A future "tighten DEV"
    /// change that switched ERP off would be a different decision, not a stricter version of this one.
    /// </summary>
    [Fact]
    public void The_DEV_config_blocks_the_webshop_and_still_permits_ERP()
    {
        using var db = NewDb();
        var guard = new ExternalWriteGuard(db, new ExternalWriteOptions
        {
            AllowExternalWrites = false,
            ExternalWrites =
            {
                ["Zoho"] = false, ["LinkedIn"] = false, ["Webshop"] = false,
                ["Erp"] = true, ["SharePoint"] = true,
            },
        });

        Assert.False(guard.IsPermittedInThisEnvironment(ExternalSystems.Webshop));
        Assert.True(guard.IsPermittedInThisEnvironment(ExternalSystems.Erp));
    }

    /// <summary>
    /// 🔴 THE HALF THAT PROTECTS PROD. PROD carries NO per-system keys, so the ceiling falls back to
    /// <c>AllowExternalWrites = true</c>. If that fallback ever inverted, this job would stop running
    /// in production and the only symptom would be sponsors quietly never appearing in the webshop.
    /// </summary>
    [Fact]
    public void The_PROD_config_permits_the_webshop_through_the_host_wide_default()
    {
        using var db = NewDb();
        var guard = new ExternalWriteGuard(db, new ExternalWriteOptions { AllowExternalWrites = true });

        Assert.Empty(new ExternalWriteOptions { AllowExternalWrites = true }.ExternalWrites);
        Assert.True(guard.IsPermittedInThisEnvironment(ExternalSystems.Webshop));
    }

    // ---- the job itself ----------------------------------------------------

    [Fact]
    public async Task A_host_that_may_not_write_to_the_webshop_does_not_run_the_reconcile()
    {
        var http = new CountingHandler();
        var erp = new CountingErp();
        var email = new CountingEmail();
        var svc = NewService(http, erp, email, webshopPermitted: false);

        var result = await svc.SyncAsync();

        Assert.False(result.Enabled);
        Assert.Equal(0, result.Customers);

        // 🔑 THE THREE THINGS HE ACTUALLY ASKED FOR, each asserted separately because each has its
        // own way of coming back: no webshop call, no e-conomic read, and above all NO E-MAIL.
        Assert.Equal(0, http.Calls);
        Assert.Equal(0, erp.ListCustomerCalls);
        Assert.Equal(0, email.Sends);

        // ⚠️ And no notes — a "skipped" note would be reported by any caller that mails on
        // `AlertNotes`, which is the same unwanted e-mail one octave down.
        Assert.Empty(result.AlertNotes);
    }

    /// <summary>
    /// 🔴 The gate must be a GATE, not an off switch. Without this, deleting the job's body would
    /// pass the test above. Same config PROD runs.
    /// </summary>
    [Fact]
    public async Task A_host_that_MAY_write_still_runs_the_reconcile_normally()
    {
        var http = new CountingHandler();
        var erp = new CountingErp();
        var email = new CountingEmail();
        var svc = NewService(http, erp, email, webshopPermitted: true);

        var result = await svc.SyncAsync();

        Assert.True(result.Enabled);
        Assert.Equal(1, result.Customers);
        Assert.Equal(1, erp.ListCustomerCalls);
        Assert.True(http.Calls > 0, "the reconcile must still reach the webshop when permitted");
    }

    // ---- fixture -----------------------------------------------------------

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"erpws-{Guid.NewGuid():N}")
            .Options);

    private static ErpWebshopContactSyncService NewService(
        CountingHandler http, CountingErp erp, CountingEmail email, bool webshopPermitted)
    {
        var cm = new CompanyManagerClient(new HttpClient(http), new CompanyManagerOptions
        {
            Enabled = true,
            BaseUrl = "https://cm.test/wp-json/company-manager/v1",
            Username = "u",
            Password = "p",
        });

        return new ErpWebshopContactSyncService(
            new EconomicContactAdminService(erp), cm, new CompanyManagerOptions { Enabled = true },
            email, NullLogger<ErpWebshopContactSyncService>.Instance,
            writes: new FixedGuard(webshopPermitted));
    }

    /// <summary>
    /// Reports one posture for every system and permits each individual write, so the ONLY thing
    /// separating the two tests above is the environment ceiling this section is about.
    /// </summary>
    private sealed class FixedGuard(bool permitted) : IExternalWriteGuard
    {
        public Task<bool> AllowAsync(string system, string operation, CancellationToken ct = default) =>
            Task.FromResult(permitted);

        public Task<bool> IsAllowedAsync(CancellationToken ct = default) => Task.FromResult(permitted);

        public bool IsPermittedInThisEnvironment(string system) => permitted;
    }

    /// <summary>Counts every outbound webshop call; the reconcile's first is GET /companies.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            var path = req.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string body) =>
                new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

            if (req.Method == HttpMethod.Get && path.EndsWith("/companies"))
                return Task.FromResult(Json(
                    "[{\"id\":1,\"erp_customer_number\":\"100\",\"name\":\"Alpha\","
                    + "\"default_signer_id\":0,\"event_coordination_default_contact_id\":0}]"));

            if (req.Method == HttpMethod.Get && path.Contains("/companies/") && path.EndsWith("/users"))
                return Task.FromResult(Json("[]"));

            if (req.Method == HttpMethod.Post && path.EndsWith("/users"))
                return Task.FromResult(Json("{\"user_id\":999}"));

            return Task.FromResult(Json("{}"));
        }
    }

    /// <summary>One sponsor customer with one contact holding both roles.</summary>
    private sealed class CountingErp : IEconomicContactAdminClient
    {
        public int ListCustomerCalls { get; private set; }
        public bool CanWrite => true;

        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default)
        {
            ListCustomerCalls++;
            return Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(
                new[] { new EconomicCustomerRow(100, "Alpha", "a@alpha.test") });
        }

        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(
                new[] { new EconomicContactRow(1, "Alpha Person", "a@alpha.test", "+45", "Role:1,2") });

        public Task<int> CreateContactAsync(int c, EconomicContactInput i, CancellationToken ct = default) => Task.FromResult(1);
        public Task UpdateContactAsync(int c, int n, EconomicContactInput i, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteContactAsync(int c, int n, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CountingEmail : IEmailSender
    {
        public int Sends { get; private set; }
        public Task SendAsync(string to, string s, string h, CancellationToken ct = default) { Sends++; return Task.CompletedTask; }
        public Task SendAsync(string to, string s, string h, IReadOnlyCollection<string>? cc, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendAsync(string to, string s, string h, string t, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithIcsAsync(string to, string s, string h, string ics, string fn, CancellationToken ct = default) => SendAsync(to, s, h, ct);
        public Task SendWithAttachmentsAsync(string to, string s, string h, IReadOnlyCollection<EmailAttachment> a, CancellationToken ct = default) => SendAsync(to, s, h, ct);
    }
}
