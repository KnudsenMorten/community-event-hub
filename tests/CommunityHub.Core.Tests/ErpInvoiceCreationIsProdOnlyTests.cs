using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1119 — AN INVOICE IS CREATED IN PRODUCTION OR NOWHERE.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-21, having said it eight times: <i>"no erp create of invoice from dev. this
/// can only happend on prod"</i> · <i>"go through coupon module and verify that no places you try to
/// create invoice in erp when on the dev env, only prod"</i>.</para>
///
/// <para>⚠️ <b>The hole this closes was one app setting wide.</b> The JOBS host swaps in
/// <see cref="TestModeEconomicInvoiceClient"/> when TestMode is on, so DEV's sweeps never reached
/// e-conomic — and that is where I kept looking. But the WEB host registers the LIVE client
/// <b>unconditionally</b>, with real tokens against the real agreement, so
/// <c>/Organizer/CouponInvoicing</c>'s "Create Invoice" button was held back by nothing except
/// <c>Invoicing:DryRun</c> defaulting to true on a host that never sets it. A default is not a
/// policy: one app setting, and DEV invoices a real customer.</para>
///
/// <para>🔑 <b>Why the guard is at the CLIENT, not at each caller.</b> Every invoice CEH has ever
/// created — the coupon sweep, the coupon prepaid button, the webshop sweep — goes through
/// <see cref="LiveEconomicInvoiceClient.CreateDraftInvoiceAsync"/>, and so will the next one. That
/// makes "no invoices from DEV" provable from one place instead of audited from many.</para>
/// </remarks>
public sealed class ErpInvoiceCreationIsProdOnlyTests
{
    /// <summary>
    /// 🔒 Fails the test if anything reaches the network. The assertion is not merely "it threw" —
    /// it is "it threw BEFORE talking to e-conomic".
    /// </summary>
    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"The client called e-conomic ({request.Method} {request.RequestUri}) — the §1119 "
                + "gate did not hold.");
    }

    /// <summary>DEV as it is really configured (verified against both app services 2026-08-21).</summary>
    private static ExternalWriteOptions DevPolicy() => new()
    {
        AllowExternalWrites = false,
        ExternalWrites = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Zoho"] = false,
            ["LinkedIn"] = false,
            ["Erp"] = true,          // §1037 — DEV may read/write ERP customers, contacts and orders…
            ["SharePoint"] = true,
        },
    };

    private static LiveEconomicInvoiceClient Client(IExternalWriteGuard guard) =>
        new(new HttpClient(new ExplodingHandler()),
            new EconomicErpOptions
            {
                Enabled = true,
                ApiBaseUrl = "https://restapi.invalid",
                AppSecretToken = "not-a-real-token",
                AgreementGrantToken = "not-a-real-token",
            },
            NullLogger<LiveEconomicInvoiceClient>.Instance,
            guard);

    private static EconomicDraftInvoice AnInvoice() => new(
        Customer: new EconomicCustomerDetail(
            1234, "A partner", "Testvej 1", "1000", "København", "DK", "DKK",
            1, null, 1, null, null, null, null),
        Date: new DateOnly(2026, 8, 21),
        LayoutNumber: 1,
        LayoutSelf: null,
        Currency: "DKK",
        OtherReference: "CEH-COUPON-1",
        VendorEmployeeNumber: 1,
        AttentionContactNumber: null,
        YourReferenceContactNumber: null,
        Heading: "Tickets",
        TextLine1: "Coupon",
        Lines: new[] { new EconomicInvoiceLine(1, "3 ticket(s)", 3, 3000m, null) },
        SalesPersonEmployeeNumber: null);

    /// <summary>
    /// DEV: e-conomic writes are allowed, and creating an invoice still is not. The two must be
    /// separately decidable or the operator's rule cannot be expressed at all.
    /// </summary>
    [Fact]
    public async Task Dev_cannot_create_an_invoice_even_though_it_may_write_to_erp()
    {
        var guard = new StubGuard(DevPolicy());
        var client = Client(guard);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateDraftInvoiceAsync(AnInvoice()));

        Assert.Contains("may not CREATE invoices", ex.Message);
        Assert.Contains("PROD only", ex.Message);
    }

    /// <summary>
    /// PROD: the same call is permitted by the guard and goes on to talk to e-conomic — proven by the
    /// handler exploding. ⚠️ Without this half, "DEV cannot invoice" would also be satisfied by a
    /// client that can never invoice anywhere.
    /// </summary>
    [Fact]
    public async Task Prod_is_permitted_and_proceeds_to_the_api()
    {
        var guard = new StubGuard(new ExternalWriteOptions { AllowExternalWrites = true });
        var client = Client(guard);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateDraftInvoiceAsync(AnInvoice()));

        Assert.Contains("The client called e-conomic", ex.Message);
    }

    /// <summary>
    /// The config-only half of the guard, with no database in the way: the same decision the real
    /// <see cref="ExternalWriteGuard"/> makes, minus the per-edition override.
    /// </summary>
    private sealed class StubGuard : IExternalWriteGuard
    {
        private readonly ExternalWriteOptions _o;
        public StubGuard(ExternalWriteOptions o) => _o = o;

        public Task<bool> AllowAsync(string system, string operation, CancellationToken ct = default)
            => Task.FromResult(IsPermittedInThisEnvironment(system));

        public Task<bool> IsAllowedAsync(CancellationToken ct = default)
            => Task.FromResult(_o.AllowExternalWrites);

        public bool IsPermittedInThisEnvironment(string system) =>
            _o.ExternalWrites.TryGetValue(ExternalSystems.ConfigKeyFor(system), out var v)
                ? v
                : _o.AllowExternalWrites;
    }
}
