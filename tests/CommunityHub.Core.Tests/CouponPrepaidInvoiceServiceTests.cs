using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §990 — <see cref="CouponPrepaidInvoiceService"/>: the invoice the "Create Invoice" button raises
/// for a PREPAID ticket purchase.
/// </summary>
/// <remarks>
/// <para>⚠️ Every assertion here is about real money on a partner's invoice, and this path had no
/// production behaviour to compare against — §795.2 said the hub NEVER raised this invoice, and the
/// operator reversed that on 2026-08-09. These tests are the whole safety net.</para>
/// </remarks>
public sealed class CouponPrepaidInvoiceServiceTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 20, 9, 0, 0, TimeSpan.Zero);

    private sealed class FakeInvoiceClient : IEconomicInvoiceClient
    {
        private readonly EconomicCustomerDetail? _customer;
        private readonly bool _throwOnCreate;

        public FakeInvoiceClient(
            bool canWrite = true, EconomicCustomerDetail? customer = null, bool throwOnCreate = false)
        {
            CanWrite = canWrite;
            _customer = customer;
            _throwOnCreate = throwOnCreate;
        }

        public bool CanWrite { get; }
        public List<EconomicDraftInvoice> Created { get; } = new();

        public Task<IReadOnlyCollection<string>> ListInvoicedOrderReferencesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

        public Task<IReadOnlyCollection<EconomicInvoiceReference>> ListInvoiceReferencesAsync(
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<EconomicInvoiceReference>>(
                Array.Empty<EconomicInvoiceReference>());

        public Task<EconomicCustomerDetail?> GetCustomerAsync(int n, CancellationToken ct = default)
            => Task.FromResult(_customer is not null && _customer.CustomerNumber == n ? _customer : null);

        public Task<(int LayoutNumber, string? Self)?> FindLayoutAsync(string nameLike, CancellationToken ct = default)
            => Task.FromResult<(int, string?)?>((7, "layout/7"));

        public Task<int> CreateDraftInvoiceAsync(EconomicDraftInvoice invoice, CancellationToken ct = default)
        {
            if (_throwOnCreate) throw new EconomicApiException("customer is blocked");
            Created.Add(invoice);
            return Task.FromResult(5100 + Created.Count);
        }
    }

    private sealed class FakeFx : IFxRateProvider
    {
        private readonly decimal? _rate;
        public FakeFx(bool canQuote, decimal? rate = null) { CanQuote = canQuote; _rate = rate; }
        public bool CanQuote { get; }
        public Task<decimal?> GetRateAsync(string b, string q, CancellationToken ct) =>
            Task.FromResult(string.Equals(b, q, StringComparison.OrdinalIgnoreCase) ? 1m : _rate);
    }

    private static EconomicCustomerDetail Customer(int number = 4242, string currency = "DKK") =>
        new(number, "Partner A/S", null, null, null, "DK", currency, 1, null, 1, null, null, null, null);

    private static CouponInvoicingSetting Rule(string? notes = null, int? customer = 4242) => new()
    {
        Id = 11,
        EventId = 1,
        CouponName = "ARROW-FI",
        BillingType = CouponBillingType.AllocatedPrepaymentByCustomer,
        ErpCustomerNumber = customer,
        Notes = notes,
    };

    private static CouponPrepaidInvoiceService NewService(
        FakeInvoiceClient client, bool dryRun = false, IFxRateProvider? fx = null) =>
        new(client, fx ?? new FakeFx(canQuote: false),
            new EconomicErpOptions { InvoiceLayoutNameLike = "Dansk" },
            new InvoicingOptions { DryRun = dryRun },
            new FixedClock(Now),
            NullLogger<CouponPrepaidInvoiceService>.Instance);

    /// <summary>
    /// 🔴 THE ONE THAT PROTECTS THE PARTNER'S INVOICE: N tickets, ONE line, the typed price.
    /// </summary>
    [Fact]
    public async Task Creates_one_line_for_the_whole_purchase_at_the_typed_price()
    {
        var client = new FakeInvoiceClient(customer: Customer());

        var r = await NewService(client).CreateForPurchaseAsync(
            Rule(), "2-day (Pre-day + Main Event)", purchaseId: 77, quantity: 20,
            unitPriceDkk: 3495.00m);

        Assert.True(r.Created);
        Assert.Equal(5101, r.DraftNumber);
        Assert.Null(r.Problem);

        var invoice = Assert.Single(client.Created);

        // ONE line covering all 20 — not 20 identical blocks. A claim invoice is one line per
        // attendee because each names a person; a prepayment names nobody yet.
        var line = Assert.Single(invoice.Lines);
        Assert.Equal(20m, line.Quantity);
        Assert.Equal(3495.00m, line.UnitNetPrice);
        Assert.Equal(69900m, (line.Quantity ?? 0m) * (line.UnitNetPrice ?? 0m));

        Assert.Contains("Prepaid tickets: 20", line.Description);
        Assert.Contains("2-day (Pre-day + Main Event)", line.Description);
        Assert.Contains("ARROW-FI", line.Description);
        // 🔒 No attendee/e-mail/ticket-id labels — a prepaid purchase has none of them, and empty
        // labels on a partner's invoice read as a broken document.
        Assert.DoesNotContain("Attendee:", line.Description);
        Assert.DoesNotContain("Zoho TicketId:", line.Description);

        Assert.Equal("DKK", invoice.Currency);
        // The reference is per PURCHASE and carries its own prefix (see the next test).
        Assert.Equal("CouponPrepaid-77", invoice.OtherReference);
    }

    /// <summary>
    /// 🔒 The prepaid marker must never collide with the CLAIM marker, or the "already invoiced"
    /// scan could read a prepayment as covering the tickets it later pays for — and the partner is
    /// billed twice for one seat.
    /// </summary>
    [Fact]
    public void The_prepaid_reference_prefix_is_distinct_from_the_claim_one()
    {
        Assert.NotEqual(
            CouponDraftInvoiceService.CouponReferencePrefix,
            CouponPrepaidInvoiceService.PrepaidReferencePrefix);
        Assert.StartsWith(
            CouponPrepaidInvoiceService.PrepaidReferencePrefix,
            CouponPrepaidInvoiceService.ReferenceFor(5), StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔴 A ZERO PRICE IS REFUSED, NOT SENT. e-conomic accepts a 0.00 line happily, and a partner
    /// invoice for 0.00 DKK is discovered only by the person who receives it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task A_missing_or_negative_price_is_refused_and_nothing_is_sent(int price)
    {
        var client = new FakeInvoiceClient(customer: Customer());

        var r = await NewService(client).CreateForPurchaseAsync(
            Rule(), "2-day", purchaseId: 1, quantity: 20, unitPriceDkk: price);

        Assert.False(r.Created);
        Assert.Empty(client.Created);
        Assert.Contains("agreed unit price", r.Problem);
    }

    [Fact]
    public async Task No_customer_on_the_coupon_means_no_invoice_and_a_reason()
    {
        var client = new FakeInvoiceClient(customer: Customer());

        var r = await NewService(client).CreateForPurchaseAsync(
            Rule(customer: null), "2-day", 1, 20, 3495m);

        Assert.False(r.Created);
        Assert.Empty(client.Created);
        Assert.Contains("no e-conomic customer", r.Problem);
        // ⚠️ The wording must say the tickets survived — the organizer has to know the click was
        // not wasted, or they click again and buy the tickets twice.
        Assert.Contains("tickets are recorded", r.Problem);
    }

    [Fact]
    public async Task An_unconfigured_economic_refuses_without_pretending_to_have_written()
    {
        var client = new FakeInvoiceClient(canWrite: false, customer: Customer());

        var r = await NewService(client).CreateForPurchaseAsync(Rule(), "2-day", 1, 20, 3495m);

        Assert.False(r.Created);
        Assert.Null(r.DraftNumber);
        Assert.Empty(client.Created);
    }

    /// <summary>
    /// §788 — a dry run composes everything and writes nothing, and must NOT report a number.
    /// </summary>
    [Fact]
    public async Task A_dry_run_creates_nothing_and_returns_no_draft_number()
    {
        var client = new FakeInvoiceClient(customer: Customer());

        var r = await NewService(client, dryRun: true)
            .CreateForPurchaseAsync(Rule(), "2-day", 1, 20, 3495m);

        Assert.Empty(client.Created);
        Assert.Null(r.DraftNumber);
        Assert.False(r.Created);
        Assert.True(r.WouldCreate);
        Assert.Contains("Dry run", r.Problem);
    }

    /// <summary>
    /// 🔒 e-conomic refusing must fail SOFT with the reason — the purchase row is already saved and
    /// the §795.2 chase will keep asking for a number, which is the right outcome.
    /// </summary>
    [Fact]
    public async Task An_economic_refusal_is_reported_not_thrown()
    {
        var client = new FakeInvoiceClient(customer: Customer(), throwOnCreate: true);

        var r = await NewService(client).CreateForPurchaseAsync(Rule(), "2-day", 1, 20, 3495m);

        Assert.False(r.Created);
        Assert.Contains("customer is blocked", r.Problem);
        Assert.Contains("recorded", r.Problem);
    }

    /// <summary>
    /// §990 item 2 — the coupon's NOTES reach the invoice (operator: *"it can be for eample purchase
    /// order number or other relevant info needed"*).
    /// </summary>
    [Fact]
    public async Task The_coupon_notes_are_printed_on_the_invoice()
    {
        var client = new FakeInvoiceClient(customer: Customer());

        await NewService(client).CreateForPurchaseAsync(
            Rule(notes: "PO 4711 — registration fee"), "2-day", 1, 20, 3495m);

        var invoice = Assert.Single(client.Created);
        Assert.Contains("Coupon tickets: ARROW-FI", invoice.TextLine1);
        Assert.Contains(CouponInvoiceLineComposer.NotesLabel, invoice.TextLine1);
        Assert.Contains("PO 4711 — registration fee", invoice.TextLine1);

        // 🔒 NOT in references.other — that carries the idempotency marker every "already invoiced"
        // check scans for, and operator prose there would break the interlock.
        Assert.DoesNotContain("PO 4711", invoice.OtherReference);
    }

    [Fact]
    public async Task No_notes_prints_no_label()
    {
        var client = new FakeInvoiceClient(customer: Customer());

        await NewService(client).CreateForPurchaseAsync(Rule(notes: "   "), "2-day", 1, 20, 3495m);

        var invoice = Assert.Single(client.Created);
        // An empty "Notes:" on a customer's invoice reads as text that failed to load (§786.1).
        Assert.DoesNotContain(CouponInvoiceLineComposer.NotesLabel, invoice.TextLine1);
    }

    /// <summary>
    /// A foreign-currency customer converts from DKK with the §786.1(f) note, exactly as the claim
    /// invoice does — the two must not price the same partner differently.
    /// </summary>
    [Fact]
    public async Task A_foreign_currency_customer_is_converted_and_the_note_is_printed()
    {
        var client = new FakeInvoiceClient(customer: Customer(currency: "EUR"));

        var r = await NewService(client, fx: new FakeFx(canQuote: true, rate: 0.134m))
            .CreateForPurchaseAsync(Rule(), "2-day", 1, 20, 3495m);

        Assert.True(r.Created);
        var invoice = Assert.Single(client.Created);
        Assert.Equal("EUR", invoice.Currency);

        var line = Assert.Single(invoice.Lines);
        Assert.Equal(Math.Round(3495m * 0.134m, 2, MidpointRounding.AwayFromZero), line.UnitNetPrice);
        Assert.Contains("Currency conversion applied", line.Description);
    }

    [Fact]
    public async Task A_foreign_currency_customer_with_no_rate_source_is_NOT_invoiced()
    {
        var client = new FakeInvoiceClient(customer: Customer(currency: "EUR"));

        var r = await NewService(client, fx: new FakeFx(canQuote: false))
            .CreateForPurchaseAsync(Rule(), "2-day", 1, 20, 3495m);

        Assert.False(r.Created);
        Assert.Empty(client.Created);
        Assert.Contains("exchange-rate source", r.Problem);
    }

    /// <summary>§798.1 — the requester is the invoice's Att person here too.</summary>
    [Fact]
    public async Task The_requester_is_passed_as_the_attention_contact()
    {
        var client = new FakeInvoiceClient(customer: Customer());
        var rule = Rule();
        rule.RequesterContactNumber = 909;

        await NewService(client).CreateForPurchaseAsync(rule, "2-day", 1, 20, 3495m);

        var invoice = Assert.Single(client.Created);
        Assert.Equal(909, invoice.AttentionContactNumber);
        Assert.Null(invoice.YourReferenceContactNumber);   // §786.1(b) is a webshop decision
    }
}
