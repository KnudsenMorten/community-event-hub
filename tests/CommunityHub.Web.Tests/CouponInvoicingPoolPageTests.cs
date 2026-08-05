using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Pages.Organizer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §795.1/§795.2/§795.4 — the coupon-invoicing page's PREPAID half: closing and re-opening a pool,
/// recording the e-conomic invoice number that confirms the prepayment was billed, and reading the
/// invoice numbers of coupon invoices back out of e-conomic.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"we need a state of pool (open, closed when used in full). we need to
/// link an invoice number manually from erp for prepaid as confirmation it was billed. otherwise
/// reminder mails"* … *"new feature ability to see invoice number of sent invoices for coupon
/// invoices"*.</para>
///
/// <para>FAKE names only.</para>
/// </remarks>
public sealed class CouponInvoicingPoolPageTests
{
    private const int EventId = 41;
    private const string TwoDay = "14880000003485482";
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class HttpContextAccessorOver(HttpContext ctx) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => ctx; set { } }
    }

    /// <summary>
    /// §795.4 — an e-conomic that knows about one BOOKED and one DRAFT invoice. Only the invoice
    /// seam is faked; everything else on the page is CEH's own data.
    /// </summary>
    private sealed class FakeInvoiceClient : IEconomicInvoiceClient
    {
        private readonly IReadOnlyCollection<EconomicInvoiceReference> _refs;
        private readonly bool _throws;

        public FakeInvoiceClient(
            IReadOnlyCollection<EconomicInvoiceReference>? refs = null, bool throws = false)
        {
            _refs = refs ?? Array.Empty<EconomicInvoiceReference>();
            _throws = throws;
        }

        public bool CanWrite => true;

        public Task<IReadOnlyCollection<EconomicInvoiceReference>> ListInvoiceReferencesAsync(
            CancellationToken ct = default)
            => _throws
                ? throw new InvalidOperationException("e-conomic is down")
                : Task.FromResult(_refs);

        public Task<IReadOnlyCollection<string>> ListInvoicedOrderReferencesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<string>>(_refs.Select(r => r.Reference).ToList());

        public Task<EconomicCustomerDetail?> GetCustomerAsync(int n, CancellationToken ct = default)
            => Task.FromResult<EconomicCustomerDetail?>(null);

        public Task<(int LayoutNumber, string? Self)?> FindLayoutAsync(string nameLike, CancellationToken ct = default)
            => Task.FromResult<(int, string?)?>(null);

        public Task<int> CreateDraftInvoiceAsync(EconomicDraftInvoice invoice, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    // One order, one claimed ticket on PARTNER-X of the 2-day class.
    private const string OrderJson = """
    {
      "id": "9001",
      "cost": { "promo_code": "PARTNER-X" },
      "tickets": [
        { "id": "t-1", "promo_code": "PARTNER-X", "ticket_name": "2-day",
          "ticket_class_id": "14880000003485482", "base_price": 3500.00, "total": 0,
          "contact": { "email": "a@example.test", "first_name": "A", "last_name": "One" } }
      ]
    }
    """;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"coupon-page-{Guid.NewGuid():N}").Options);

    private static ClaimsPrincipal Session(Participant p) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, p.Id.ToString()),
            new Claim(ClaimTypes.Email, p.Email),
            new Claim(ClaimTypes.Name, p.FullName),
            new Claim(ClaimTypes.Role, p.Role.ToString()),
            new Claim("EventId", p.EventId.ToString()),
        }, CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task<(Participant Organizer, CouponPrepaidAllocation Pool)> SeedAsync(
        CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CI27", CommunityName = "C", DisplayName = "CI 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.Orders.Add(new Order { EventId = EventId, RawJson = OrderJson });
        await db.SaveChangesAsync();

        var org = new Participant
        {
            EventId = EventId, FullName = "Olivia Organizer", Email = "olivia@example.test",
            Role = ParticipantRole.Organizer, IsActive = true,
        };
        db.Participants.Add(org);

        var rule = new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-X",
            BillingType = CouponBillingType.AllocatedPrepaymentByCustomer, ErpCustomerNumber = 4242,
        };
        db.CouponInvoicingSettings.Add(rule);
        await db.SaveChangesAsync();

        var pool = new CouponPrepaidAllocation
        {
            EventId = EventId, CouponInvoicingSettingId = rule.Id,
            TicketClassId = TwoDay, TicketClassLabel = "2-day",
            CreatedAt = Now.AddDays(-5),
            // §798.4 — five tickets bought in one purchase, not yet invoiced.
            Purchases = new List<CouponPrepaidPurchase>
            {
                new() { Quantity = 5, CreatedAt = Now.AddDays(-5) },
            },
        };
        db.CouponPrepaidAllocations.Add(pool);
        await db.SaveChangesAsync();
        return (org, pool);
    }

    /// <summary>
    /// §798.1 — an e-conomic whose customer 4242 has two contacts, and customer 9999 has one. Only
    /// the contact-admin surface is faked; the coupon data is CEH's own.
    /// </summary>
    private sealed class FakeContactClient : IEconomicContactAdminClient
    {
        public bool CanWrite => true;

        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(new[]
            {
                new EconomicCustomerRow(4242, "Partner A/S", null),
                new EconomicCustomerRow(9999, "Someone Else ApS", null),
            });

        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EconomicContactRow>>(customerNumber switch
            {
                4242 => new[]
                {
                    new EconomicContactRow(11, "Rita Requester", "rita@example.test", null),
                    new EconomicContactRow(12, "Sam Signer", "sam@example.test", null),
                },
                9999 => new[] { new EconomicContactRow(77, "Other Person", null, null) },
                _ => Array.Empty<EconomicContactRow>(),
            });

        public Task<int> CreateContactAsync(int c, EconomicContactInput i, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateContactAsync(int c, int n, EconomicContactInput i, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteContactAsync(int c, int n, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>§797.3 — TempData is required now that every POST redirects.</summary>
    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context)
            => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private static CouponInvoicingModel NewModel(
        CommunityHubDbContext db, HttpContext http, IEconomicInvoiceClient? invoices = null,
        IEconomicContactAdminClient? economic = null) =>
        new(new HttpCurrentParticipantAccessor(new HttpContextAccessorOver(http)),
            db, new FixedClock(), economic: economic, invoices: invoices)
        {
            PageContext = new PageContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NullTempDataProvider()),
        };

    // ---------------------------------------------------------------------
    //  §795.1 — closing and re-opening
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Closing_a_pool_records_who_closed_it_and_why()
    {
        using var db = NewDb();
        var (org, pool) = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.PoolAllocationId = pool.Id;
        model.PoolClosedReason = "Agreement ended after the 2027 edition.";

        var result = await model.OnPostClosePoolAsync(reopen: false, default);

        // §797.3 — POST → REDIRECT → GET: F5 must not re-submit.
        Assert.IsType<RedirectToPageResult>(result);
        var saved = await db.CouponPrepaidAllocations.SingleAsync();
        Assert.Equal(Now, saved.ClosedAt);
        Assert.Equal("olivia@example.test", saved.ClosedByEmail);
        Assert.Equal("Agreement ended after the 2027 edition.", saved.ClosedReason);

        // The flash says the promo code is still live in Backstage — closing here does not close it
        // there. It travels in TempData now, because the redirect throws the page model away.
        Assert.Contains("Backstage", model.Flash);
    }

    /// <summary>
    /// 🔴 The one that matters: a HUMAN close survives the arithmetic. The pool has 4 of 5 left, so
    /// nothing derived would ever call it closed.
    /// </summary>
    [Fact]
    public async Task A_closed_pool_reads_as_closed_on_the_page_even_with_tickets_left()
    {
        using var db = NewDb();
        var (org, pool) = await SeedAsync(db);
        pool.ClosedAt = Now.AddDays(-1);
        pool.ClosedByEmail = "olivia@example.test";
        await db.SaveChangesAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        await model.OnGetAsync(default);

        var row = Assert.Single(model.Rows, r => r.CouponName == "PARTNER-X");
        var balance = Assert.Single(row.Pools).Balance;
        Assert.Equal(CouponPoolState.ClosedByHand, balance.State);
        Assert.Equal(4, balance.Remaining);          // one claimed of five — still tickets left
        Assert.False(balance.IsLow);                 // ...and no "running low" nag about a decision
    }

    [Fact]
    public async Task Re_opening_clears_the_close_completely()
    {
        using var db = NewDb();
        var (org, pool) = await SeedAsync(db);
        pool.ClosedAt = Now.AddDays(-1);
        pool.ClosedByEmail = "olivia@example.test";
        pool.ClosedReason = "Paused";
        await db.SaveChangesAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.PoolAllocationId = pool.Id;

        await model.OnPostClosePoolAsync(reopen: true, default);

        var saved = await db.CouponPrepaidAllocations.SingleAsync();
        Assert.Null(saved.ClosedAt);
        Assert.Null(saved.ClosedByEmail);
        Assert.Null(saved.ClosedReason);
    }

    // ---------------------------------------------------------------------
    //  §795.2 — the prepayment's invoice number
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Recording_the_invoice_number_marks_the_purchase_billed()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);
        var purchase = await db.CouponPrepaidPurchases.SingleAsync();
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.PoolPurchaseId = purchase.Id;
        model.PoolErpInvoiceNumber = "  20147 ";

        await model.OnPostSetPoolInvoiceAsync(default);

        var saved = await db.CouponPrepaidPurchases.SingleAsync();
        Assert.Equal("20147", saved.ErpInvoiceNumber);     // trimmed, kept verbatim otherwise
        Assert.Equal(Now, saved.ErpInvoiceConfirmedAt);
        Assert.Equal("olivia@example.test", saved.ErpInvoiceConfirmedByEmail);
        Assert.True(saved.IsBilled);
    }

    /// <summary>
    /// 🔒 Clearing a wrong number must not buy another week of silence — the reminder stamp is reset
    /// so the next hourly pass chases it again.
    /// </summary>
    [Fact]
    public async Task Clearing_the_invoice_number_restarts_the_chase()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);
        var purchase = await db.CouponPrepaidPurchases.SingleAsync();
        purchase.ErpInvoiceNumber = "20147";
        purchase.ErpInvoiceConfirmedAt = Now.AddDays(-2);
        purchase.LastBillingReminderAt = Now.AddDays(-2);
        await db.SaveChangesAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.PoolPurchaseId = purchase.Id;
        model.PoolErpInvoiceNumber = "   ";

        await model.OnPostSetPoolInvoiceAsync(default);

        var saved = await db.CouponPrepaidPurchases.SingleAsync();
        Assert.Null(saved.ErpInvoiceNumber);
        Assert.Null(saved.ErpInvoiceConfirmedAt);
        Assert.Null(saved.LastBillingReminderAt);
        Assert.False(saved.IsBilled);
    }

    // ---------------------------------------------------------------------
    //  §798.4 — topping a pool up under the same coupon code
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔴 *"I must be able to extend a prepaid pool (increase amount) … but dont want a new coupon
    /// code. Then i will invoice him for fx 25 more as invoice #."*
    /// </summary>
    [Fact]
    public async Task Buying_more_tickets_adds_a_purchase_under_the_same_coupon()
    {
        using var db = NewDb();
        var (org, pool) = await SeedAsync(db);
        var rule = await db.CouponInvoicingSettings.SingleAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.SettingId = rule.Id;
        model.PoolTicketClassId = TwoDay;
        model.PoolQuantity = 25;
        model.PoolErpInvoiceNumber = "20233";

        await model.OnPostAddTicketsAsync(default);

        // 🔒 ONE pool, TWO purchases — same coupon code, two agreements, two invoices.
        Assert.Single(await db.CouponPrepaidAllocations.ToListAsync());
        var purchases = await db.CouponPrepaidPurchases.OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(2, purchases.Count);
        Assert.Equal(5, purchases[0].Quantity);
        Assert.Equal(25, purchases[1].Quantity);
        Assert.Equal("20233", purchases[1].ErpInvoiceNumber);
        Assert.Equal(Now, purchases[1].ErpInvoiceConfirmedAt);

        await model.OnGetAsync(default);
        var balance = Assert.Single(
            Assert.Single(model.Rows, r => r.CouponName == "PARTNER-X").Pools).Balance;
        Assert.Equal(30, balance.Purchased);       // 5 + 25, summed and not stored
        Assert.Equal(29, balance.Remaining);       // one claimed
    }

    /// <summary>
    /// 🔴 The pool is not billed while ANY purchase is missing its number — the case a single field
    /// could not express: the first 5 were invoiced, the 25 top-up was not.
    /// </summary>
    [Fact]
    public async Task A_top_up_leaves_the_pool_unbilled_until_it_has_its_own_invoice_number()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);
        var first = await db.CouponPrepaidPurchases.SingleAsync();
        first.ErpInvoiceNumber = "20147";
        var rule = await db.CouponInvoicingSettings.SingleAsync();
        await db.SaveChangesAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.SettingId = rule.Id;
        model.PoolTicketClassId = TwoDay;
        model.PoolQuantity = 25;                    // no invoice number for the top-up

        await model.OnPostAddTicketsAsync(default);
        await model.OnGetAsync(default);

        var row = Assert.Single(model.Rows, r => r.CouponName == "PARTNER-X");
        var balance = Assert.Single(row.Pools).Balance;
        Assert.Equal(1, balance.UnbilledPurchases);
        Assert.False(balance.IsBilled);
        Assert.True(row.HasUnbilledPool);
    }

    /// <summary>
    /// ⚠️ Quantity 0 no longer means "delete the pool" — with top-ups that reading would make a typo
    /// destroy an agreement. Removing is its own button.
    /// </summary>
    [Fact]
    public async Task Buying_zero_tickets_is_refused_rather_than_deleting_the_pool()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);
        var rule = await db.CouponInvoicingSettings.SingleAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.SettingId = rule.Id;
        model.PoolTicketClassId = TwoDay;
        model.PoolQuantity = 0;

        await model.OnPostAddTicketsAsync(default);

        // §797.3 — the refusal travels as a flash; errors redirect too, or the one page somebody is
        // most likely to press F5 on would be the one that kept ?handler= in the address bar.
        Assert.NotNull(model.FlashError);
        Assert.Single(await db.CouponPrepaidAllocations.ToListAsync());
        Assert.Single(await db.CouponPrepaidPurchases.ToListAsync());
    }

    [Fact]
    public async Task Removing_a_pool_takes_its_purchase_history_with_it()
    {
        using var db = NewDb();
        var (org, pool) = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.PoolAllocationId = pool.Id;

        await model.OnPostRemovePoolAsync(default);

        Assert.Empty(await db.CouponPrepaidAllocations.ToListAsync());
        Assert.Empty(await db.CouponPrepaidPurchases.ToListAsync());
    }

    /// <summary>An unbilled prepaid pool is surfaced at the top, like an unmapped coupon.</summary>
    [Fact]
    public async Task An_unbilled_prepaid_pool_needs_attention()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });

        await model.OnGetAsync(default);

        var row = Assert.Single(model.Rows, r => r.CouponName == "PARTNER-X");
        Assert.True(row.HasUnbilledPool);
        Assert.True(row.NeedsAttention);
    }

    // ---------------------------------------------------------------------
    //  §799 — a pool left over from a previous billing type must not count
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔴 *"i chaned type of coupon from claimed to prepaid and back to claimed. then count was still
    /// showing even after page refresh. i had to delete/add coupon again"*.
    /// </summary>
    [Fact]
    public async Task A_pool_on_a_coupon_that_is_no_longer_prepaid_stops_counting()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);
        var rule = await db.CouponInvoicingSettings.SingleAsync();
        rule.BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer;
        await db.SaveChangesAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        await model.OnGetAsync(default);

        var row = Assert.Single(model.Rows, r => r.CouponName == "PARTNER-X");
        Assert.Empty(row.Pools);                 // no balance, no chips
        Assert.False(row.HasPoolTrouble);
        Assert.False(row.HasUnbilledPool);       // ...and it stops dragging the coupon to the top
        Assert.False(row.NeedsAttention);

        // 🔒 But the record of what was bought is NOT destroyed by a dropdown change.
        Assert.Single(row.DormantPools);
        Assert.Equal(5, Assert.Single(row.DormantPools).Balance.Purchased);
        Assert.Single(await db.CouponPrepaidAllocations.ToListAsync());
    }

    /// <summary>Switching back to prepaid brings the pool back exactly as it was.</summary>
    [Fact]
    public async Task Switching_back_to_prepaid_restores_the_pool()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);
        var rule = await db.CouponInvoicingSettings.SingleAsync();

        rule.BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer;
        await db.SaveChangesAsync();
        rule.BillingType = CouponBillingType.AllocatedPrepaymentByCustomer;
        await db.SaveChangesAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        await model.OnGetAsync(default);

        var row = Assert.Single(model.Rows, r => r.CouponName == "PARTNER-X");
        var balance = Assert.Single(row.Pools).Balance;
        Assert.Equal(5, balance.Purchased);
        Assert.Equal(4, balance.Remaining);       // one claimed, unchanged by the round trip
        Assert.Empty(row.DormantPools);
    }

    // ---------------------------------------------------------------------
    //  §797.3 — post → redirect → get
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔴 *"…/CouponInvoicing?handler=Save"* — every POST now redirects, so a refresh re-runs the
    /// GET instead of re-submitting the form.
    /// </summary>
    [Fact]
    public async Task Every_post_redirects_rather_than_rendering_the_page()
    {
        using var db = NewDb();
        var (org, pool) = await SeedAsync(db);
        var rule = await db.CouponInvoicingSettings.SingleAsync();
        var purchase = await db.CouponPrepaidPurchases.SingleAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.SettingId = rule.Id;
        model.CouponName = "PARTNER-X";
        model.BillingType = CouponBillingType.AllocatedPrepaymentByCustomer;
        model.ErpCustomerNumber = 4242;
        model.PoolAllocationId = pool.Id;
        model.PoolPurchaseId = purchase.Id;
        model.PoolTicketClassId = TwoDay;
        model.PoolQuantity = 5;

        Assert.IsType<RedirectToPageResult>(await model.OnPostSaveAsync(default));
        Assert.IsType<RedirectToPageResult>(await model.OnPostAddTicketsAsync(default));
        Assert.IsType<RedirectToPageResult>(await model.OnPostSetPoolThresholdAsync(default));
        Assert.IsType<RedirectToPageResult>(await model.OnPostSetPoolInvoiceAsync(default));
        Assert.IsType<RedirectToPageResult>(await model.OnPostClosePoolAsync(reopen: false, default));
        Assert.IsType<RedirectToPageResult>(await model.OnPostRemovePoolAsync(default));
        Assert.IsType<RedirectToPageResult>(await model.OnPostDeleteAsync(default));
    }

    /// <summary>The flash survives the redirect and is shown by the following GET.</summary>
    [Fact]
    public async Task The_outcome_message_survives_the_redirect()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);
        var purchase = await db.CouponPrepaidPurchases.SingleAsync();

        var http = new DefaultHttpContext { User = Session(org) };
        var tempData = new TempDataDictionary(http, new NullTempDataProvider());

        var posting = NewModel(db, http);
        posting.TempData = tempData;
        posting.PoolPurchaseId = purchase.Id;
        posting.PoolErpInvoiceNumber = "20147";
        await posting.OnPostSetPoolInvoiceAsync(default);

        // The next request is a fresh page model sharing only TempData — which is the point.
        var showing = NewModel(db, http);
        showing.TempData = tempData;
        await showing.OnGetAsync(default);

        Assert.Contains("20147", showing.Message);
        Assert.Null(showing.Error);
    }

    /// <summary>
    /// ⚠️ A refused NEW coupon keeps the name he typed — it is the only input on the page that is not
    /// re-rendered from its own row, and the redirect would otherwise throw it away.
    /// </summary>
    [Fact]
    public async Task A_refused_new_coupon_keeps_the_typed_name()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) });
        model.SettingId = 0;                      // the ADD form
        model.CouponName = "NEW-PARTNER-2027";
        model.BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer;
        model.ErpCustomerNumber = null;           // ...which needs a customer

        await model.OnPostSaveAsync(default);

        Assert.NotNull(model.FlashError);
        Assert.Equal("NEW-PARTNER-2027", model.DraftCouponName);
    }

    // ---------------------------------------------------------------------
    //  §798.1 — the requester (the invoice's Att person)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task The_requester_is_saved_with_the_name_read_from_economic()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);
        var rule = await db.CouponInvoicingSettings.SingleAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) },
            economic: new FakeContactClient());
        model.SettingId = rule.Id;
        model.CouponName = "PARTNER-X";
        model.BillingType = CouponBillingType.AllocatedPrepaymentByCustomer;
        model.ErpCustomerNumber = 4242;
        model.RequesterContactNumber = 11;

        await model.OnPostSaveAsync(default);

        var saved = await db.CouponInvoicingSettings.SingleAsync();
        Assert.Equal(11, saved.RequesterContactNumber);
        Assert.Equal("Rita Requester", saved.RequesterName);
    }

    /// <summary>
    /// 🔴 A contact from ANOTHER customer is dropped, not saved. e-conomic would reject it — or
    /// worse, accept it against a different account, which is a wrong name printed on a real invoice.
    /// </summary>
    [Fact]
    public async Task A_requester_from_a_different_customer_is_refused()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);
        var rule = await db.CouponInvoicingSettings.SingleAsync();

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) },
            economic: new FakeContactClient());
        model.SettingId = rule.Id;
        model.CouponName = "PARTNER-X";
        model.BillingType = CouponBillingType.AllocatedPrepaymentByCustomer;
        model.ErpCustomerNumber = 4242;
        model.RequesterContactNumber = 77;          // belongs to customer 9999

        await model.OnPostSaveAsync(default);

        var saved = await db.CouponInvoicingSettings.SingleAsync();
        Assert.Null(saved.RequesterContactNumber);
        Assert.Null(saved.RequesterName);
    }

    /// <summary>The dropdown offers this coupon's own customer's contacts, and only those.</summary>
    [Fact]
    public async Task The_page_loads_the_contacts_of_the_coupons_own_customer()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) },
            economic: new FakeContactClient());
        await model.OnGetAsync(default);

        Assert.True(model.ContactsByCustomer.ContainsKey(4242));
        Assert.Equal(2, model.ContactsByCustomer[4242].Count);
        Assert.False(model.ContactsByCustomer.ContainsKey(9999));   // nobody's coupon bills them
    }

    // ---------------------------------------------------------------------
    //  §798.3 — his words on the billing types
    // ---------------------------------------------------------------------

    [Fact]
    public void The_billing_types_are_shown_in_the_operators_words()
    {
        Assert.Equal("Prepaid tickets with ticket-pool",
            CouponInvoicingModel.DisplayName(CouponBillingType.AllocatedPrepaymentByCustomer));
        Assert.Equal("Ad-hoc invoicing when claimed",
            CouponInvoicingModel.DisplayName(CouponBillingType.ClaimableAdHocPaymentByCustomer));

        // 🔒 DISPLAY ONLY — the stored values are untouched, because the sweep, the pool and the
        // alerts all read them as integers.
        Assert.Equal(1, (int)CouponBillingType.AllocatedPrepaymentByCustomer);
        Assert.Equal(2, (int)CouponBillingType.ClaimableAdHocPaymentByCustomer);
    }

    // ---------------------------------------------------------------------
    //  §795.4 — the invoice numbers of coupon invoices
    // ---------------------------------------------------------------------

    [Fact]
    public async Task The_page_shows_booked_and_draft_invoice_numbers_for_a_coupon()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);

        // The reference CEH writes for this claim, as CouponClaimExtractor builds it.
        var reference = CouponClaimExtractor.BuildReference("PARTNER-X", "9001");
        var client = new FakeInvoiceClient(new[]
        {
            new EconomicInvoiceReference(reference, 1042, false),
            new EconomicInvoiceReference(reference, 20147, true),
            new EconomicInvoiceReference("WebshopOrderId-4711", 20200, true),   // not ours
        });

        var model = NewModel(db, new DefaultHttpContext { User = Session(org) }, client);
        await model.OnGetAsync(default);

        var row = Assert.Single(model.Rows, r => r.CouponName == "PARTNER-X");
        Assert.Equal(2, row.Invoices.Count);
        Assert.True(row.Invoices[0].IsBooked);                       // booked first — the real number
        Assert.Equal("invoice 20147 (booked)", row.Invoices[0].Display);
        Assert.Equal("draft 1042", row.Invoices[1].Display);
        Assert.True(model.EconomicReachable);
    }

    /// <summary>
    /// ⚠️ "No invoice number" and "we could not ask e-conomic" are different statements. The page
    /// keeps working and says which one it is.
    /// </summary>
    [Fact]
    public async Task An_unreachable_economic_does_not_break_the_page_and_is_named()
    {
        using var db = NewDb();
        var (org, _) = await SeedAsync(db);
        var model = NewModel(db, new DefaultHttpContext { User = Session(org) },
            new FakeInvoiceClient(throws: true));

        var result = await model.OnGetAsync(default);

        Assert.IsType<PageResult>(result);
        Assert.False(model.EconomicReachable);
        Assert.Empty(Assert.Single(model.Rows, r => r.CouponName == "PARTNER-X").Invoices);
    }
}
