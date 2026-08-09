using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §787 — <see cref="CouponDraftInvoiceService"/>: the half that decides whether a partner is
/// invoiced, for how much, and what happens when nobody has said who pays.
/// </summary>
/// <remarks>
/// <para>⚠️ Every assertion here is about real money. The retired script has NEVER RUN (§787.5), so
/// unlike §786 there is no production behaviour to compare against — these tests are the only thing
/// standing between a partner and a wrong invoice.</para>
/// </remarks>
public sealed class CouponDraftInvoiceServiceTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2027, 1, 20, 9, 0, 0, TimeSpan.Zero);

    // One order, two tickets on the SAME coupon. base_price is 3500/2000 and total is 0 — the real
    // shape §787.6 measured on the live feed, where the coupon covered the ticket entirely.
    private const string OrderJson = """
    {
      "id": "9001",
      "created_time": "2027-01-10T09:00:00Z",
      "cost": { "promo_code": "PARTNER-X" },
      "tickets": [
        { "id": "t-1", "promo_code": "PARTNER-X", "ticket_name": "2-day",
          "base_price": 3500.00, "total": 0,
          "contact": { "email": "a@example.test", "first_name": "A", "last_name": "One" } },
        { "id": "t-2", "promo_code": "PARTNER-X", "ticket_name": "1-day",
          "base_price": 2000.00, "total": 0,
          "contact": { "email": "b@example.test", "first_name": "B", "last_name": "Two" } }
      ]
    }
    """;

    private sealed class FakeInvoiceClient : IEconomicInvoiceClient
    {
        private readonly EconomicCustomerDetail? _customer;
        public FakeInvoiceClient(bool canWrite = true, EconomicCustomerDetail? customer = null,
            IReadOnlyCollection<string>? alreadyInvoiced = null)
        {
            CanWrite = canWrite;
            _customer = customer;
            Already = alreadyInvoiced ?? Array.Empty<string>();
        }

        public bool CanWrite { get; }
        public IReadOnlyCollection<string> Already { get; }
        public List<EconomicDraftInvoice> Created { get; } = new();

        public Task<IReadOnlyCollection<string>> ListInvoicedOrderReferencesAsync(CancellationToken ct = default)
            => Task.FromResult(Already);

        /// <summary>§795.4 — the same set, numbered. Booked, because "already invoiced" is what it means.</summary>
        public Task<IReadOnlyCollection<EconomicInvoiceReference>> ListInvoiceReferencesAsync(
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<EconomicInvoiceReference>>(
                Already.Select((r, i) => new EconomicInvoiceReference(r, 20000 + i, true)).ToList());

        public Task<EconomicCustomerDetail?> GetCustomerAsync(int n, CancellationToken ct = default)
            => Task.FromResult(_customer is not null && _customer.CustomerNumber == n ? _customer : null);

        public Task<(int LayoutNumber, string? Self)?> FindLayoutAsync(string nameLike, CancellationToken ct = default)
            => Task.FromResult<(int, string?)?>((7, "layout/7"));

        public Task<int> CreateDraftInvoiceAsync(EconomicDraftInvoice invoice, CancellationToken ct = default)
        {
            Created.Add(invoice);
            return Task.FromResult(Created.Count);
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

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"coupon-inv-{Guid.NewGuid():N}").Options);

    private static async Task SeedOrderAsync(CommunityHubDbContext db, string json = OrderJson)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CI27", CommunityName = "C", DisplayName = "Coupon 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.Orders.Add(new Order { EventId = EventId, RawJson = json });
        await db.SaveChangesAsync();
    }

    private static CouponDraftInvoiceService NewService(
        CommunityHubDbContext db, FakeInvoiceClient invoices, bool dryRun = false,
        IFxRateProvider? fx = null,
        // §1016d — 0 keeps the PRE-EXISTING per-pass behaviour, so every test written before the
        // billing period existed still describes the same run. Only the §1016d tests set a period.
        int intervalDays = 0) =>
        new(db, invoices, fx ?? new FakeFx(canQuote: false),
            new EconomicErpOptions { InvoiceLayoutNameLike = "Dansk" },
            new InvoicingOptions { DryRun = dryRun, CouponInvoiceIntervalDays = intervalDays },
            new FixedClock(Now),
            NullLogger<CouponDraftInvoiceService>.Instance);

    // ================= §1016d: the fortnightly billing period ==================
    //
    // Operator 2026-08-09: *"the claim gets registered so fx a ticket claim for 2 tickets decreases
    // from 20 to 18. But we dont want to invoice customer for every single claim; that creates to
    // many invoices. therefore you must batch them to every 2 weeks and remember when the last
    // invoice was sent for this coupon, so you know the 'catch-up' to invoice."*
    //
    // 🔑 The CLAIM was never the problem — the balance always moved immediately, and still does.
    // What ran too often was the INVOICE: the job passes every ~10 minutes and billed whatever was
    // new, so a coupon claimed on ten different days produced ten invoices.

    /// <summary>Seeds an invoiceable ad-hoc coupon, optionally already invoiced / with its own period.</summary>
    private static async Task SeedBillablePartnerAsync(
        CommunityHubDbContext db, DateTimeOffset? lastInvoicedAt = null,
        DateTimeOffset? firstSeen = null, int? perCouponDays = null)
    {
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
            LastInvoicedAt = lastInvoicedAt,
            FirstSeenClaimedAt = firstSeen,
            InvoiceIntervalDays = perCouponDays,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Claims_wait_for_the_billing_period_instead_of_invoicing_immediately()
    {
        // Invoiced 3 days ago, period 14 ⇒ the new claims accumulate. Not a problem, not an alert:
        // reporting this would be alerting him about the system doing exactly what he asked for.
        using var db = NewDb();
        await SeedOrderAsync(db);
        await SeedBillablePartnerAsync(db, lastInvoicedAt: Now.AddDays(-3));
        var client = new FakeInvoiceClient(customer: Customer());

        var result = await NewService(db, client, intervalDays: 14).RunAsync(EventId);

        Assert.Equal(0, result.Created);
        Assert.Empty(client.Created);
        Assert.Empty(result.Problems);          // waiting is not a fault
    }

    [Fact]
    public async Task The_whole_catch_up_is_invoiced_together_when_the_period_opens()
    {
        // 🔑 The "catch-up" needs no bookkeeping of its own: `pending` is already every claim not
        // yet on a booked or draft invoice, so when the window opens the batch IS everything since
        // the last invoice — including anything an earlier failed pass left behind.
        using var db = NewDb();
        await SeedOrderAsync(db);
        await SeedBillablePartnerAsync(db, lastInvoicedAt: Now.AddDays(-15));
        var client = new FakeInvoiceClient(customer: Customer());

        var result = await NewService(db, client, intervalDays: 14).RunAsync(EventId);

        Assert.Equal(1, result.Created);
        var invoice = Assert.Single(client.Created);
        Assert.Equal(2, invoice.Lines.Count);   // BOTH accumulated claims, on ONE invoice

        // …and the clock restarts, so the next fortnight is measured from now.
        Assert.Equal(Now, (await db.CouponInvoicingSettings.SingleAsync()).LastInvoicedAt);
    }

    [Fact]
    public async Task A_never_invoiced_coupon_measures_the_period_from_its_first_claim()
    {
        // Otherwise claim #1 would get an invoice to itself and only the REST would ever be batched
        // — which is the "too many invoices" complaint, just moved one claim later.
        using var db = NewDb();
        await SeedOrderAsync(db);
        await SeedBillablePartnerAsync(db, lastInvoicedAt: null, firstSeen: Now.AddDays(-2));
        var client = new FakeInvoiceClient(customer: Customer());

        Assert.Equal(0, (await NewService(db, client, intervalDays: 14).RunAsync(EventId)).Created);
    }

    [Fact]
    public async Task A_per_coupon_period_overrides_the_edition_default()
    {
        // Operator 2026-08-09: *"maybe the internal days could be a field that could be adjusted pr
        // coupon"*. A billing period is negotiated per partner; one global number would force the
        // strictest partner's terms onto everybody. Here: default 14 would WAIT, this partner's 2
        // does not.
        using var db = NewDb();
        await SeedOrderAsync(db);
        await SeedBillablePartnerAsync(db, lastInvoicedAt: Now.AddDays(-3), perCouponDays: 2);
        var client = new FakeInvoiceClient(customer: Customer());

        Assert.Equal(1, (await NewService(db, client, intervalDays: 14).RunAsync(EventId)).Created);
    }

    [Fact]
    public async Task A_per_coupon_period_of_zero_invoices_every_pass()
    {
        // 0 means the same thing on both settings — "no batching" — so the two cannot be read
        // differently. This is the escape hatch for closing a period early.
        using var db = NewDb();
        await SeedOrderAsync(db);
        await SeedBillablePartnerAsync(db, lastInvoicedAt: Now.AddDays(-1), perCouponDays: 0);
        var client = new FakeInvoiceClient(customer: Customer());

        Assert.Equal(1, (await NewService(db, client, intervalDays: 14).RunAsync(EventId)).Created);
    }

    /// <summary>
    /// 🔒 A DRY RUN must NOT stamp the clock. It writes no invoice, so stamping would push the next
    /// REAL invoice out by a whole fortnight — silently, and only visible a partner-complaint later.
    /// </summary>
    [Fact]
    public async Task A_dry_run_never_starts_the_billing_clock()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        await SeedBillablePartnerAsync(db, lastInvoicedAt: Now.AddDays(-15));
        var client = new FakeInvoiceClient(customer: Customer());

        await NewService(db, client, dryRun: true, intervalDays: 14).RunAsync(EventId);

        Assert.Empty(client.Created);
        Assert.Equal(Now.AddDays(-15), (await db.CouponInvoicingSettings.SingleAsync()).LastInvoicedAt);
    }

    /// <summary>
    /// 🔴 THE ONE THAT PROTECTS THE PARTNER'S INVOICE: bill `base_price`, never `total`.
    /// </summary>
    /// <remarks>
    /// §787.6 measured this on the live feed — both coupon tickets have `total` 0 because the coupon
    /// covered them entirely. An implementation that billed `total` would send the partner an
    /// invoice for 0.00 DKK for every coupon ticket, and nothing downstream would notice.
    /// </remarks>
    [Fact]
    public async Task It_bills_the_full_ticket_price_not_the_discounted_total()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
        });
        await db.SaveChangesAsync();

        var client = new FakeInvoiceClient(customer: Customer());
        var result = await NewService(db, client).RunAsync(EventId);

        Assert.Equal(1, result.Created);                 // ONE invoice per coupon, not per ticket
        var invoice = Assert.Single(client.Created);
        Assert.Equal(2, invoice.Lines.Count);            // ...with one line per claimed ticket

        // 3500 + 2000 = 5500, NOT 0.
        Assert.Equal(5500m, invoice.Lines.Sum(l => (l.Quantity ?? 0m) * (l.UnitNetPrice ?? 0m)));
        Assert.Equal("DKK", invoice.Currency);
        // §814 — the HEADING is the house line (the same one the webshop invoices carry, approved on
        // the first invoice he sent); the COUPON NAME sits on the line below it. A coupon invoice
        // headed "Coupon tickets: PARTNER-X" would have reached a partner looking like it came from
        // a different company than their sponsorship invoice.
        Assert.Equal("ELDK27 - Experts Live Denmark", invoice.Heading);
        Assert.Contains("PARTNER-X", invoice.TextLine1);
    }

    /// <summary>
    /// 🔴 An unmapped coupon is NEVER guessed at — the money belongs to somebody.
    /// </summary>
    [Fact]
    public async Task An_unmapped_coupon_is_reported_and_never_invoiced()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);   // no CouponInvoicingSetting at all

        var client = new FakeInvoiceClient(customer: Customer());
        var result = await NewService(db, client).RunAsync(EventId);

        Assert.Empty(client.Created);
        Assert.Equal(0, result.Created);
        Assert.Contains("PARTNER-X", result.UnmappedCoupons);
        Assert.Contains(result.Problems, p => p.Contains("nobody has said who pays"));

        // ⚠️ The Unmapped row is CREATED, with the date it was first seen — a missing row cannot be
        // shown, chased or acted on, which is the whole reason the mapping became a table.
        var row = await db.CouponInvoicingSettings.SingleAsync(c => c.CouponName == "PARTNER-X");
        Assert.Equal(CouponBillingType.Unmapped, row.BillingType);
        Assert.Equal(Now, row.FirstSeenClaimedAt);
    }

    /// <summary>
    /// A billing type that needs a customer but has none must be refused, not defaulted to anyone.
    /// </summary>
    [Fact]
    public async Task A_billing_type_with_no_customer_is_refused()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = null,
        });
        await db.SaveChangesAsync();

        var client = new FakeInvoiceClient(customer: Customer());
        var result = await NewService(db, client).RunAsync(EventId);

        Assert.Empty(client.Created);
        Assert.Contains("PARTNER-X", result.UnmappedCoupons);
    }

    /// <summary>
    /// 🔒 `NoInvoicing` is a DECISION, not a gap: silent, and never alerted on. Keeping it distinct
    /// from Unmapped is the entire point of having both values.
    /// </summary>
    [Fact]
    public async Task NoInvoicing_is_silent_and_is_not_reported_as_a_problem()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.NoInvoicing,
        });
        await db.SaveChangesAsync();

        var client = new FakeInvoiceClient(customer: Customer());
        var result = await NewService(db, client).RunAsync(EventId);

        Assert.Empty(client.Created);
        Assert.Empty(result.UnmappedCoupons);
        Assert.Empty(result.Problems);
    }

    /// <summary>Idempotency: a claim already on a booked or draft invoice is never billed twice.</summary>
    [Fact]
    public async Task A_claim_already_invoiced_is_skipped()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
        });
        await db.SaveChangesAsync();

        // Both claims already carry a reference e-conomic knows about.
        var refs = CouponClaimExtractor.FromOrderJson(OrderJson).Select(c => c.Reference).ToList();
        var client = new FakeInvoiceClient(customer: Customer(), alreadyInvoiced: refs);

        var result = await NewService(db, client).RunAsync(EventId);

        Assert.Empty(client.Created);
        Assert.Equal(2, result.AlreadyInvoiced);
        Assert.Equal(0, result.Created);
    }

    /// <summary>
    /// §788 — a DRY RUN composes everything for real and writes nothing.
    /// 🔒 `Created` must read 0: it is the number an operator takes at face value.
    /// </summary>
    [Fact]
    public async Task A_dry_run_composes_everything_and_writes_nothing()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
        });
        await db.SaveChangesAsync();

        var client = new FakeInvoiceClient(customer: Customer());
        var result = await NewService(db, client, dryRun: true).RunAsync(EventId);

        Assert.Empty(client.Created);            // nothing written
        Assert.Equal(0, result.Created);         // and nothing CLAIMED to be written
        Assert.True(result.DryRun);
        var would = Assert.Single(result.WouldCreateOrEmpty);
        Assert.Contains("PARTNER-X", would);
        Assert.Contains("4242", would);          // it names the customer it WOULD have billed
        Assert.Contains("5500", would);          // ...and the real amount, not 0
    }

    /// <summary>
    /// ⚠️ A foreign-currency customer with no rate source is REFUSED, never billed DKK figures under
    /// a foreign symbol — the one failure here that reaches a partner as a wrong number rather than
    /// as a missing invoice.
    /// </summary>
    [Fact]
    public async Task A_foreign_currency_customer_with_no_rate_source_is_refused()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
        });
        await db.SaveChangesAsync();

        var client = new FakeInvoiceClient(customer: Customer(currency: "EUR"));
        var result = await NewService(db, client, fx: new FakeFx(canQuote: false)).RunAsync(EventId);

        Assert.Empty(client.Created);
        Assert.Contains(result.Problems, p => p.Contains("no exchange-rate source is configured"));
    }

    /// <summary>With a rate, the conversion happens and the note names both currencies.</summary>
    [Fact]
    public async Task A_foreign_currency_customer_is_converted_when_a_rate_exists()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
        });
        await db.SaveChangesAsync();

        var client = new FakeInvoiceClient(customer: Customer(currency: "EUR"));
        var result = await NewService(db, client, fx: new FakeFx(true, 0.134m)).RunAsync(EventId);

        Assert.Equal(1, result.Created);
        var invoice = Assert.Single(client.Created);
        Assert.Equal("EUR", invoice.Currency);
        // 3500 * 0.134 = 469.00 and 2000 * 0.134 = 268.00 — 2 decimals, NOT the webshop's Ceiling.
        Assert.Equal(737.00m, invoice.Lines.Sum(l => (l.Quantity ?? 0m) * (l.UnitNetPrice ?? 0m)));
        Assert.Contains(invoice.Lines, l => l.Description.Contains("DKK"));
    }

    /// <summary>Unconfigured e-conomic is INACTIVE with a reason, never a silent no-op.</summary>
    [Fact]
    public async Task Unconfigured_economic_reports_why_nothing_happened()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);

        var result = await NewService(db, new FakeInvoiceClient(canWrite: false)).RunAsync(EventId);

        Assert.Equal(0, result.Created);
        Assert.Contains(result.Problems, p => p.Contains("e-conomic is not configured"));
    }

    /// <summary>
    /// §798.1 — THE REQUESTER BECOMES THE INVOICE'S ATT PERSON.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-04: *"i also need to add a requester per coupon (dropdown from erp contacts)
    /// which is the att person for the invoice"*. Before this, a coupon invoice passed no attention
    /// at all and fell back to whatever the customer record pointed at — which is why nobody could
    /// steer it. ⚠️ "Your reference" is deliberately left alone: he asked for the Att person.
    /// </remarks>
    [Fact]
    public async Task The_requester_is_the_invoices_attention_contact()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
            RequesterContactNumber = 11, RequesterName = "Rita Requester",
        });
        await db.SaveChangesAsync();

        var client = new FakeInvoiceClient(customer: Customer());
        await NewService(db, client).RunAsync(EventId);

        var invoice = Assert.Single(client.Created);
        Assert.Equal(11, invoice.AttentionContactNumber);
        // ⚠️ NOT "Your reference": he asked for the Att person, and §786.1(b) is a separate decision
        // made for webshop invoices. Applying it here would change what prints on a partner's
        // invoice without being asked.
        Assert.Null(invoice.YourReferenceContactNumber);
    }

    /// <summary>
    /// 🔒 A coupon with no requester is UNCHANGED: no attention is sent and e-conomic falls back to
    /// the customer's own contact, exactly as every coupon invoice did before §798.1.
    /// </summary>
    [Fact]
    public async Task A_coupon_without_a_requester_sends_no_attention_at_all()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
        });
        await db.SaveChangesAsync();

        var client = new FakeInvoiceClient(customer: Customer());
        await NewService(db, client).RunAsync(EventId);

        Assert.Null(Assert.Single(client.Created).AttentionContactNumber);
    }

    /// <summary>
    /// §795.3 — a created draft is described well enough to MAIL: what it is for, who is billed, how
    /// much, and the draft number to find it by.
    /// </summary>
    [Fact]
    public async Task A_created_draft_is_recorded_for_the_info_notice()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
        });
        await db.SaveChangesAsync();

        var result = await NewService(db, new FakeInvoiceClient(customer: Customer())).RunAsync(EventId);

        var draft = Assert.Single(result.CreatedDraftsOrEmpty);
        Assert.Contains("PARTNER-X", draft.Description);
        Assert.Equal(4242, draft.CustomerNumber);
        Assert.Equal("Partner A/S", draft.CustomerName);
        Assert.Equal(5500m, draft.Total);              // base_price, as the invoice itself carries
        Assert.Equal("DKK", draft.Currency);
        Assert.Equal(1, draft.DraftNumber);            // e-conomic's number, read back from the POST
        Assert.Equal("PARTNER-X-9001", draft.Reference);
    }

    /// <summary>
    /// 🔒 §795.3 — A DRY RUN CREATES NOTHING, SO IT MUST ANNOUNCE NOTHING. A would-create is not a
    /// draft, and mailing about one would name an invoice that does not exist in e-conomic.
    /// </summary>
    [Fact]
    public async Task A_dry_run_records_no_created_drafts_to_announce()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
        });
        await db.SaveChangesAsync();

        var client = new FakeInvoiceClient(customer: Customer());
        var result = await NewService(db, client, dryRun: true).RunAsync(EventId);

        Assert.Empty(client.Created);                       // nothing was written...
        Assert.Single(result.WouldCreateOrEmpty);           // ...but it is reported as a would-create
        Assert.Empty(result.CreatedDraftsOrEmpty);          // 🔒 and nothing is announced
    }

    /// <summary>
    /// §990 item 2 — the coupon's NOTES print on the AD-HOC (claim) invoice too. Operator
    /// 2026-08-09: *"the notes must be added to the invoice, bth fo the prepaid invoice and the
    /// ad-hoc biling invoice, as it can be for eample purchase order number"*. The note is a
    /// property of the AGREEMENT, so every invoice that agreement produces carries it — one type
    /// carrying it and the other not is exactly the drift worth a test.
    /// </summary>
    [Fact]
    public async Task The_coupon_notes_are_printed_on_the_ad_hoc_invoice()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
            Notes = "PO 4711 - registration fee",
        });
        await db.SaveChangesAsync();

        var client = new FakeInvoiceClient(customer: Customer());
        await NewService(db, client).RunAsync(EventId);

        var invoice = Assert.Single(client.Created);
        Assert.Contains("Coupon tickets: PARTNER-X", invoice.TextLine1);
        Assert.Contains(CouponInvoiceLineComposer.NotesLabel, invoice.TextLine1);
        Assert.Contains("PO 4711 - registration fee", invoice.TextLine1);

        // 🔒 NOT in references.other — that is the idempotency marker the "already invoiced" scan
        // reads, and operator prose there would either overwrite it or force a substring match.
        Assert.DoesNotContain("PO 4711", invoice.OtherReference);
    }

    /// <summary>A coupon with no notes prints no label — an empty "Notes:" reads as a fault.</summary>
    [Fact]
    public async Task No_notes_prints_no_label_on_the_ad_hoc_invoice()
    {
        using var db = NewDb();
        await SeedOrderAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = 4242,
        });
        await db.SaveChangesAsync();

        var client = new FakeInvoiceClient(customer: Customer());
        await NewService(db, client).RunAsync(EventId);

        var invoice = Assert.Single(client.Created);
        Assert.DoesNotContain(CouponInvoiceLineComposer.NotesLabel, invoice.TextLine1);
    }
}
