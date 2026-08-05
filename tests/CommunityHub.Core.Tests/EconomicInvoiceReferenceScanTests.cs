using System.Net;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §795.4 — reading invoice NUMBERS back out of the scan CEH already runs.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"new feature ability to see invoice number of sent invoices for coupon
/// invoices"*.</para>
///
/// <para>🔑 No new integration: <c>ListInvoicedOrderReferencesAsync</c> already paged BOTH
/// <c>/invoices/booked</c> and <c>/invoices/drafts</c> for the idempotency interlock and threw
/// everything but the reference away. ⚠️ The trap this file pins is that the two lists carry
/// DIFFERENT number fields for the same invoice — <c>bookedInvoiceNumber</c> and
/// <c>draftInvoiceNumber</c> — and a draft number quoted to a partner is not one they will
/// recognise.</para>
/// </remarks>
public sealed class EconomicInvoiceReferenceScanTests
{
    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _byPath;
        public List<string> Requested { get; } = new();
        public RouteHandler(Dictionary<string, string> byPath) => _byPath = byPath;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var body = _byPath.FirstOrDefault(kv => url.Contains(kv.Key, StringComparison.Ordinal)).Value
                       ?? "{\"collection\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }

    private const string Booked = """
    { "collection": [
        { "bookedInvoiceNumber": 20147, "references": { "other": "CouponTicket-ARROW-DK-9001" } },
        { "bookedInvoiceNumber": 20148, "references": { } },
        { "bookedInvoiceNumber": 20149, "references": { "other": "WebshopOrderId-4711" } }
      ] }
    """;

    private const string Drafts = """
    { "collection": [
        { "draftInvoiceNumber": 1042, "references": { "other": "CouponTicket-NORDIC-9002" } }
      ] }
    """;

    private static LiveEconomicInvoiceClient Make(out RouteHandler handler)
    {
        handler = new RouteHandler(new Dictionary<string, string>
        {
            ["/invoices/booked"] = Booked,
            ["/invoices/drafts"] = Drafts,
        });

        return new LiveEconomicInvoiceClient(
            new HttpClient(handler),
            new EconomicErpOptions
            {
                Enabled = true,
                ApiBaseUrl = "https://restapi.e-conomic.com",
                AppSecretToken = "secret",
                AgreementGrantToken = "grant",
            },
            NullLogger<LiveEconomicInvoiceClient>.Instance);
    }

    /// <summary>
    /// 🔴 A BOOKED number and a DRAFT number come from different fields, and which one it is has to
    /// travel with the number.
    /// </summary>
    [Fact]
    public async Task Booked_and_draft_numbers_are_read_from_their_own_fields()
    {
        var client = Make(out _);

        var refs = await client.ListInvoiceReferencesAsync();

        var coupon = Assert.Single(refs, r => r.Reference == "CouponTicket-ARROW-DK-9001");
        Assert.Equal(20147, coupon.Number);
        Assert.True(coupon.IsBooked);
        Assert.Equal("invoice 20147 (booked)", coupon.Display);

        var draft = Assert.Single(refs, r => r.Reference == "CouponTicket-NORDIC-9002");
        Assert.Equal(1042, draft.Number);
        Assert.False(draft.IsBooked);
        Assert.Equal("draft 1042", draft.Display);
    }

    /// <summary>An invoice with no <c>references.other</c> is not ours and is skipped.</summary>
    [Fact]
    public async Task Invoices_without_our_reference_are_ignored()
    {
        var client = Make(out _);

        var refs = await client.ListInvoiceReferencesAsync();

        Assert.Equal(3, refs.Count);                       // 20148 carries no reference
        Assert.DoesNotContain(refs, r => r.Number == 20148);
    }

    /// <summary>
    /// 🔒 The idempotency interlock is DERIVED from the same scan, so the numbers on the page and
    /// the "already invoiced" check can never disagree — and BOTH lists are still read, because a
    /// draft that has been booked disappears from /drafts.
    /// </summary>
    [Fact]
    public async Task The_reference_only_scan_still_covers_booked_and_drafts()
    {
        var client = Make(out var handler);

        var refs = await client.ListInvoicedOrderReferencesAsync();

        Assert.Contains("CouponTicket-ARROW-DK-9001", refs);   // booked
        Assert.Contains("CouponTicket-NORDIC-9002", refs);     // draft
        Assert.Contains("WebshopOrderId-4711", refs);
        Assert.Contains(handler.Requested, u => u.Contains("/invoices/booked"));
        Assert.Contains(handler.Requested, u => u.Contains("/invoices/drafts"));
    }
}
