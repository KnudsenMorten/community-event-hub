using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1013a — a stored e-conomic invoice number must follow the invoice through BOOKING.
/// </summary>
/// <remarks>
/// <para><b>The incident.</b> Operator 2026-08-09: *"you mentioned invoice 182, but the actual
/// number is 170"*, then, minutes later, *"i have sent invoice now and it got invoice 170"*.</para>
///
/// <para>✅ Verified live against e-conomic (agreement 1685551): <c>/invoices/drafts</c> held NO
/// <c>CouponPrepaid-*</c> row at all, while <c>/invoices/booked</c> held <c>bookedInvoiceNumber</c>
/// <b>170</b> for <c>CouponPrepaid-1</c>. CEH held <b>182</b>.</para>
///
/// <para>🔑 <b>Nothing misread anything — the value went STALE.</b> e-conomic numbers a draft from
/// one series and re-numbers it from another on booking, so 182 was right when captured and died
/// the moment he booked. The knowledge was already written on
/// <c>EconomicInvoiceReference.IsBooked</c> (*"a DRAFT number is provisional and is replaced when a
/// human books the invoice"*) and nothing acted on it.</para>
/// </remarks>
public sealed class CouponPrepaidInvoiceNumberRefresherTests
{
    private const int EventId = 1;

    /// <summary>An e-conomic that reports exactly the references it was handed.</summary>
    private sealed class FakeInvoiceClient : IEconomicInvoiceClient
    {
        private readonly IReadOnlyCollection<EconomicInvoiceReference> _refs;
        private readonly bool _throws;

        public FakeInvoiceClient(
            IReadOnlyCollection<EconomicInvoiceReference>? refs = null,
            bool canWrite = true, bool throws = false)
        {
            _refs = refs ?? Array.Empty<EconomicInvoiceReference>();
            CanWrite = canWrite;
            _throws = throws;
        }

        public bool CanWrite { get; }

        public Task<IReadOnlyCollection<EconomicInvoiceReference>> ListInvoiceReferencesAsync(
            CancellationToken ct = default) =>
            _throws
                ? throw new EconomicApiException("e-conomic is having a day")
                : Task.FromResult(_refs);

        public Task<IReadOnlyCollection<string>> ListInvoicedOrderReferencesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<string>>(_refs.Select(r => r.Reference).ToList());
        public Task<EconomicCustomerDetail?> GetCustomerAsync(int n, CancellationToken ct = default)
            => Task.FromResult<EconomicCustomerDetail?>(null);
        public Task<(int LayoutNumber, string? Self)?> FindLayoutAsync(string s, CancellationToken ct = default)
            => Task.FromResult<(int, string?)?>((7, "layout/7"));
        public Task<int> CreateDraftInvoiceAsync(EconomicDraftInvoice i, CancellationToken ct = default)
            => Task.FromResult(1);
    }

    /// <summary>Seeds one prepaid purchase carrying <paramref name="storedNumber"/>.</summary>
    private static async Task<CouponPrepaidPurchase> SeedPurchaseAsync(
        CommunityHubDbContext db, string? storedNumber, bool booked = false)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 10), EndDate = new DateOnly(2027, 2, 10),
        });
        var pool = new CouponPrepaidAllocation
        {
            EventId = EventId, CouponInvoicingSettingId = 11, TicketClassId = "tc-1",
        };
        db.CouponPrepaidAllocations.Add(pool);
        await db.SaveChangesAsync();

        var buy = new CouponPrepaidPurchase
        {
            CouponPrepaidAllocationId = pool.Id, Quantity = 20,
            ErpInvoiceNumber = storedNumber, ErpInvoiceIsBooked = booked,
        };
        db.CouponPrepaidPurchases.Add(buy);
        await db.SaveChangesAsync();
        return buy;
    }

    private static CouponPrepaidInvoiceNumberRefresher NewRefresher(
        CommunityHubDbContext db, IEconomicInvoiceClient client) => new(db, client);

    [Fact]
    public async Task A_draft_number_becomes_the_booked_number_once_it_is_booked()
    {
        // THE INCIDENT, reproduced: stored 182, e-conomic now says CouponPrepaid-{id} is booked 170.
        using var db = ScenarioFixture.NewDb();
        var buy = await SeedPurchaseAsync(db, "182");
        var client = new FakeInvoiceClient(new[]
        {
            new EconomicInvoiceReference(CouponPrepaidInvoiceService.ReferenceFor(buy.Id), 170, IsBooked: true),
        });

        var result = await NewRefresher(db, client).RefreshAsync(EventId);

        Assert.Equal(1, result.Updated);
        var after = await db.CouponPrepaidPurchases.SingleAsync();
        Assert.Equal("170", after.ErpInvoiceNumber);
        Assert.True(after.ErpInvoiceIsBooked);
    }

    [Fact]
    public async Task A_number_that_has_not_moved_is_left_alone()
    {
        // The page calls this on EVERY load, so a no-op pass must write nothing at all.
        using var db = ScenarioFixture.NewDb();
        var buy = await SeedPurchaseAsync(db, "170", booked: true);
        var client = new FakeInvoiceClient(new[]
        {
            new EconomicInvoiceReference(CouponPrepaidInvoiceService.ReferenceFor(buy.Id), 170, IsBooked: true),
        });

        var result = await NewRefresher(db, client).RefreshAsync(EventId);

        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Checked);
    }

    /// <summary>
    /// 🔒 THE GUARD THAT MATTERS MOST. A number the operator typed in by hand belongs to an invoice
    /// CEH never raised, so it carries no <c>CouponPrepaid-</c> reference and cannot match the scan.
    /// Overwriting his own confirmation with a guess would be far worse than a stale draft number.
    /// </summary>
    [Fact]
    public async Task A_hand_typed_number_is_never_overwritten()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedPurchaseAsync(db, "PO-4471/2026");
        // e-conomic has plenty of invoices — just none carrying THIS purchase's marker.
        var client = new FakeInvoiceClient(new[]
        {
            new EconomicInvoiceReference("WebshopOrderId-10726", 178, IsBooked: false),
            new EconomicInvoiceReference("CouponPrepaid-99", 171, IsBooked: true),
        });

        var result = await NewRefresher(db, client).RefreshAsync(EventId);

        Assert.Equal(0, result.Updated);
        Assert.Equal("PO-4471/2026", (await db.CouponPrepaidPurchases.SingleAsync()).ErpInvoiceNumber);
    }

    /// <summary>
    /// 🔒 §553 — "not found" is NOT "gone", and a finance reference is the worst possible place to
    /// guess. A short read, a deleted draft or a credited invoice must leave the stored number
    /// exactly as it is rather than blanking a number he may be quoting to a partner.
    /// </summary>
    [Fact]
    public async Task An_invoice_missing_from_e_conomic_never_clears_the_stored_number()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedPurchaseAsync(db, "182");
        var client = new FakeInvoiceClient(Array.Empty<EconomicInvoiceReference>());

        var result = await NewRefresher(db, client).RefreshAsync(EventId);

        Assert.Equal(0, result.Updated);
        Assert.Equal("182", (await db.CouponPrepaidPurchases.SingleAsync()).ErpInvoiceNumber);
    }

    [Fact]
    public async Task An_e_conomic_outage_changes_nothing_and_says_why()
    {
        // This runs on page load. A finance page that will not open is worse than a stale number.
        using var db = ScenarioFixture.NewDb();
        await SeedPurchaseAsync(db, "182");

        var result = await NewRefresher(db, new FakeInvoiceClient(throws: true)).RefreshAsync(EventId);

        Assert.Equal(0, result.Updated);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
        Assert.Equal("182", (await db.CouponPrepaidPurchases.SingleAsync()).ErpInvoiceNumber);
    }

    [Fact]
    public async Task An_unconfigured_e_conomic_is_skipped_rather_than_called()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedPurchaseAsync(db, "182");

        var result = await NewRefresher(db, new FakeInvoiceClient(canWrite: false)).RefreshAsync(EventId);

        Assert.Equal(0, result.Checked);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
    }

    /// <summary>
    /// §1013c — the prefill is CONFIG, and a bad app setting must leave the shipped default alone
    /// rather than take the host down (the §786.6 rule that <c>DryRun</c> already follows).
    /// </summary>
    /// <summary>
    /// 🔴 §1016a — a created prepaid draft must hand back everything the "new draft invoice" ops
    /// mail needs, or the announcement cannot be made.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-09: *"i did not get any emails about a new invoice was created"*.
    /// `DraftInvoiceCreatedNotifier` had exactly two callers, both background jobs — so the one
    /// invoice path a HUMAN triggers, by clicking a button labelled "Create Invoice", was the only
    /// one that announced nothing. 🔒 A DRY RUN must still hand back nothing: it creates no invoice,
    /// and announcing one would name a draft that does not exist.
    /// </remarks>
    [Theory]
    [InlineData(false)]   // real create ⇒ the announcement payload is present
    [InlineData(true)]    // dry run ⇒ nothing created, so nothing to announce
    public void A_created_prepaid_draft_carries_what_the_ops_mail_needs(bool dryRun)
    {
        // The record is the contract between the service and the page; assert its shape directly
        // so a field the mail needs cannot go missing without a failure.
        var payload = dryRun
            ? null
            : new CreatedDraftInvoice(
                "20 × 2-day ticket (ARROW-FI)", 341409335, "Arrow ECS Finland Oy",
                "CouponPrepaid-1", 60000m, "DKK", 182);
        var result = dryRun
            ? new PrepaidInvoiceResult(null, "dry run", WouldCreate: true)
            : new PrepaidInvoiceResult(182, null, Created_: payload);

        Assert.Equal(!dryRun, result.Created);
        Assert.Equal(dryRun ? null : payload, result.Created_);
        if (dryRun) return;

        // Everything the mail prints, so a rename cannot silently blank a column.
        Assert.Equal(182, result.Created_!.DraftNumber);
        Assert.Equal("CouponPrepaid-1", result.Created_.Reference);
        Assert.Equal("Arrow ECS Finland Oy", result.Created_.CustomerName);
        Assert.Equal(60000m, result.Created_.Total);
        Assert.Equal("DKK", result.Created_.Currency);
    }

    [Theory]
    [InlineData(null, 3000)]        // not set ⇒ the shipped default
    [InlineData("2500", 2500)]      // set ⇒ honoured
    [InlineData("not a number", 3000)]
    [InlineData("", 3000)]
    [InlineData("-5", 3000)]        // negative is not a price
    [InlineData("0", 0)]            // zero is meaningful: it switches the prefill OFF
    public void The_default_prepaid_unit_price_reads_defensively(string? configured, decimal expected)
    {
        var pairs = new List<KeyValuePair<string, string?>>();
        if (configured is not null)
            pairs.Add(new("Invoicing:DefaultPrepaidUnitPriceDkk", configured));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs).Build();

        Assert.Equal(expected, InvoicingOptions.FromConfiguration(config).DefaultPrepaidUnitPriceDkk);
    }
}
