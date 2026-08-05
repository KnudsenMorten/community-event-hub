using CommunityHub.Core.Tests.Scenario;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §795.1/§795.2 — a prepaid pool's LIFECYCLE: open, closed, and whether anyone was ever billed
/// for it.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"we need a state of pool (open, closed when used in full). we need to
/// link an invoice number manually from erp for prepaid as confirmation it was billed. otherwise
/// reminder mails"*.</para>
///
/// <para>🔴 The whole of §795.1 is one distinction: <b>a pool that RAN OUT re-opens by itself when a
/// ticket is cancelled; a pool a HUMAN closed must not.</b> Both look like "closed" on screen, and
/// only one of them is arithmetic.</para>
/// </remarks>
public sealed class CouponPoolLifecycleTests
{
    private const int EventId = 1;
    private const string TwoDay = "14880000003485482";
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public void Set(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static CouponPrepaidAllocation Pool(
        int purchased = 4, DateTimeOffset? closedAt = null, string? invoiceNumber = "20147") =>
        new()
        {
            Id = 11,
            EventId = EventId,
            CouponInvoicingSettingId = 1,
            TicketClassId = TwoDay,
            TicketClassLabel = "2-day",
            ClosedAt = closedAt,
            // §798.4 — the quantity and the invoice number live on the PURCHASE.
            Purchases = new List<CouponPrepaidPurchase>
            {
                new() { Quantity = purchased, ErpInvoiceNumber = invoiceNumber },
            },
        };

    private static CouponClaim Claim(bool cancelled = false, string id = "t") =>
        new(
            Reference: $"ARROW-DK-{id}", CouponName: "ARROW-DK", OrderId: "9001", TicketId: id,
            Email: $"{id}@example.test", FirstName: "A", LastName: "Attendee",
            TicketClassName: "2-day", UnitPriceDkk: 3500m, TicketClassId: TwoDay,
            IsCancelled: cancelled);

    // ---------------------------------------------------------------------
    //  §795.1 — the state
    // ---------------------------------------------------------------------

    [Fact]
    public void A_pool_with_tickets_left_is_open()
    {
        var b = CouponPrepaidBalance.For(Pool(purchased: 10), new[] { Claim(id: "1") });

        Assert.Equal(CouponPoolState.Open, b.State);
        Assert.False(b.IsClosed);
        Assert.Equal("open", b.StateLabel);
    }

    /// <summary>*"closed when used in full"* — derived from the arithmetic, not stored.</summary>
    [Fact]
    public void A_pool_used_in_full_reads_as_closed_without_anyone_setting_it()
    {
        var claims = Enumerable.Range(1, 4).Select(i => Claim(id: $"t{i}")).ToArray();
        var b = CouponPrepaidBalance.For(Pool(purchased: 4), claims);

        Assert.Equal(CouponPoolState.UsedInFull, b.State);
        Assert.True(b.IsClosed);
        Assert.False(b.ClosedByHand);
    }

    /// <summary>
    /// 🔒 THE HALF THAT MUST RE-OPEN. A cancellation returns the allocation, so a pool that was full
    /// is open again — no job runs, nothing is written, the number is simply re-derived.
    /// </summary>
    [Fact]
    public void A_pool_closed_by_arithmetic_re_opens_when_a_ticket_is_cancelled()
    {
        var pool = Pool(purchased: 4);
        var full = Enumerable.Range(1, 4).Select(i => Claim(id: $"t{i}")).ToArray();
        Assert.Equal(CouponPoolState.UsedInFull, CouponPrepaidBalance.For(pool, full).State);

        var oneCancelled = full.Select((c, i) => i == 0 ? c with { IsCancelled = true } : c).ToArray();
        var after = CouponPrepaidBalance.For(pool, oneCancelled);

        Assert.Equal(CouponPoolState.Open, after.State);
        Assert.Equal(1, after.Remaining);
    }

    /// <summary>
    /// 🔴 THE HALF THAT MUST NOT. An agreement somebody ENDED cannot be re-opened by a cancellation,
    /// or CEH silently hands the partner back a ticket nobody agreed to.
    /// </summary>
    [Fact]
    public void A_pool_closed_by_a_human_stays_closed_even_when_tickets_come_back()
    {
        var pool = Pool(purchased: 4, closedAt: Now);
        var claims = new[] { Claim(id: "1", cancelled: true), Claim(id: "2", cancelled: true) };

        var b = CouponPrepaidBalance.For(pool, claims);

        Assert.Equal(CouponPoolState.ClosedByHand, b.State);
        Assert.True(b.ClosedByHand);
        Assert.Equal(4, b.Remaining);          // the tickets ARE back...
        Assert.Equal("closed", b.StateLabel);  // ...and the agreement is still over
    }

    /// <summary>
    /// A closed pool is never "running low": that warning is about an agreement somebody is still
    /// claiming against, and nagging about a decision already made is the §302 failure.
    /// </summary>
    [Fact]
    public void A_closed_pool_is_not_reported_as_low()
    {
        var claims = Enumerable.Range(1, 3).Select(i => Claim(id: $"t{i}")).ToArray();

        Assert.True(CouponPrepaidBalance.For(Pool(purchased: 4), claims).IsLow);
        Assert.False(CouponPrepaidBalance.For(Pool(purchased: 4, closedAt: Now), claims).IsLow);
    }

    /// <summary>An oversubscribed pool says so, rather than reading as a tidy "used in full".</summary>
    [Fact]
    public void An_oversubscribed_pool_is_labelled_as_such()
    {
        var claims = Enumerable.Range(1, 6).Select(i => Claim(id: $"t{i}")).ToArray();
        var b = CouponPrepaidBalance.For(Pool(purchased: 4), claims);

        Assert.Equal(CouponPoolState.UsedInFull, b.State);
        Assert.Equal("oversubscribed", b.StateLabel);
    }

    // ---------------------------------------------------------------------
    //  §795.2 — the reminder that a prepayment was never confirmed billed
    // ---------------------------------------------------------------------

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"coupon-pool-{Guid.NewGuid():N}").Options);

    private static (CouponPrepaidBillingReminderService Service, CapturingEmailSender Mail)
        NewReminder(CommunityHubDbContext db, FixedClock clock)
    {
        var mail = new CapturingEmailSender();
        var alerts = new EngineAlertSender(
            mail, new EmailContextAccessor(), clock, NullLogger<EngineAlertSender>.Instance);
        return (new CouponPrepaidBillingReminderService(db, alerts, clock), mail);
    }

    private static async Task<CouponPrepaidAllocation> SeedPoolAsync(
        CommunityHubDbContext db, DateTimeOffset createdAt,
        CouponBillingType billing = CouponBillingType.AllocatedPrepaymentByCustomer,
        string? invoiceNumber = null)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CP27", CommunityName = "C", DisplayName = "Coupon pools",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });

        var rule = new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "ARROW-DK", BillingType = billing, ErpCustomerNumber = 4242,
        };
        db.CouponInvoicingSettings.Add(rule);
        await db.SaveChangesAsync();

        var pool = new CouponPrepaidAllocation
        {
            EventId = EventId,
            CouponInvoicingSettingId = rule.Id,
            TicketClassId = TwoDay,
            TicketClassLabel = "2-day",
            CreatedAt = createdAt,
            // §798.4 — the pool's 50 tickets are its first PURCHASE, and that row is what gets
            // chased for an invoice number.
            Purchases = new List<CouponPrepaidPurchase>
            {
                new() { Quantity = 50, CreatedAt = createdAt, ErpInvoiceNumber = invoiceNumber },
            },
        };
        db.CouponPrepaidAllocations.Add(pool);
        await db.SaveChangesAsync();
        return pool;
    }

    /// <summary>
    /// 🔴 THE HOLE §794 OPENED: a prepaid pool is never invoiced by CEH, so without this nothing in
    /// the system says the partner was ever charged for what they bought.
    /// </summary>
    [Fact]
    public async Task A_pool_with_no_invoice_number_is_chased()
    {
        using var db = NewDb();
        await SeedPoolAsync(db, createdAt: Now.AddDays(-3));
        var clock = new FixedClock(Now);
        var (service, mail) = NewReminder(db, clock);

        Assert.Equal(1, await service.RemindAsync(EventId));

        var sent = Assert.Single(mail.Messages);
        // §808 — an ISSUE goes to mok@, not the actionable mailbox. Operator 2026-08-04:
        // *"alert mails (issues) ... goes to mok@expertslive.dk - and notification emails about new
        // pending invoices goes to info@expertslive.dk"*. The draft-created notice is the one that
        // stays on info@, and its own test asserts that.
        Assert.Equal(EngineAlertSender.Recipient, sent.To);
        Assert.Contains("no invoice number", sent.Subject);
        Assert.Contains("ARROW-DK", sent.Html);
        Assert.Contains("50 × 2-day", sent.Html);

        // The stamp is what keeps the next hour quiet — on the PURCHASE (§798.4).
        Assert.NotNull((await db.CouponPrepaidPurchases.SingleAsync()).LastBillingReminderAt);
    }

    /// <summary>
    /// 🔴 §798.4 — THE TOP-UP IS CHASED ON ITS OWN. A partner whose first 50 were invoiced in
    /// January and who then bought 25 more must not read as settled because of the January invoice.
    /// </summary>
    [Fact]
    public async Task An_unbilled_top_up_is_chased_even_when_the_first_purchase_was_invoiced()
    {
        using var db = NewDb();
        var pool = await SeedPoolAsync(db, createdAt: Now.AddDays(-60), invoiceNumber: "20147");
        db.CouponPrepaidPurchases.Add(new CouponPrepaidPurchase
        {
            CouponPrepaidAllocationId = pool.Id,
            Quantity = 25,
            CreatedAt = Now.AddDays(-3),      // bought later, not yet invoiced
        });
        await db.SaveChangesAsync();

        var (service, mail) = NewReminder(db, new FixedClock(Now));

        Assert.Equal(1, await service.RemindAsync(EventId));

        var html = Assert.Single(mail.Messages).Html;
        Assert.Contains("25 × 2-day", html);
        Assert.Contains("top-up", html);
        Assert.DoesNotContain("50 × 2-day", html);   // the January tickets are settled
    }

    /// <summary>
    /// ⚠️ A pool typed in minutes ago is not chased — the invoice is raised by hand in e-conomic,
    /// usually not in the same minute, and mailing about it would be mailing about work in progress.
    /// </summary>
    [Fact]
    public async Task A_pool_created_within_the_grace_period_is_left_alone()
    {
        using var db = NewDb();
        await SeedPoolAsync(db, createdAt: Now.AddHours(-2));
        var (service, mail) = NewReminder(db, new FixedClock(Now));

        Assert.Equal(0, await service.RemindAsync(EventId));
        Assert.Empty(mail.Messages);
    }

    /// <summary>Entering the number stops it — immediately and permanently.</summary>
    [Fact]
    public async Task A_pool_with_an_invoice_number_is_never_chased()
    {
        using var db = NewDb();
        await SeedPoolAsync(db, createdAt: Now.AddDays(-30), invoiceNumber: "20147");
        var (service, mail) = NewReminder(db, new FixedClock(Now));

        Assert.Equal(0, await service.RemindAsync(EventId));
        Assert.Empty(mail.Messages);
    }

    /// <summary>
    /// §302 — one reminder a week, per pool. The stamp is per ROW, so a new pool is still chased in
    /// the meantime; a blanket suppression would have hidden it.
    /// </summary>
    [Fact]
    public async Task It_goes_quiet_for_a_week_and_then_speaks_up_again()
    {
        using var db = NewDb();
        await SeedPoolAsync(db, createdAt: Now.AddDays(-3));
        var clock = new FixedClock(Now);
        var (service, mail) = NewReminder(db, clock);

        Assert.Equal(1, await service.RemindAsync(EventId));

        clock.Set(Now.AddDays(3));
        Assert.Equal(0, await service.RemindAsync(EventId));
        Assert.Single(mail.Messages);

        clock.Set(Now.AddDays(8));
        Assert.Equal(1, await service.RemindAsync(EventId));
        Assert.Equal(2, mail.Messages.Count);
    }

    /// <summary>
    /// 🔒 A coupon that is no longer PREPAID is not chased for a hand-entered number: CEH invoices
    /// that billing type itself, and asking for a manual confirmation too would be asking for the
    /// same money twice.
    /// </summary>
    [Fact]
    public async Task A_pool_on_a_coupon_that_is_no_longer_prepaid_is_not_chased()
    {
        using var db = NewDb();
        await SeedPoolAsync(
            db, createdAt: Now.AddDays(-30),
            billing: CouponBillingType.ClaimableAdHocPaymentByCustomer);
        var (service, mail) = NewReminder(db, new FixedClock(Now));

        Assert.Equal(0, await service.RemindAsync(EventId));
        Assert.Empty(mail.Messages);
    }

    /// <summary>
    /// §795.1 + §795.2 are independent: closing a pool does not mean anybody billed it, so a closed
    /// pool with no number is still chased.
    /// </summary>
    [Fact]
    public async Task A_closed_pool_is_still_chased_when_nobody_recorded_an_invoice()
    {
        using var db = NewDb();
        var pool = await SeedPoolAsync(db, createdAt: Now.AddDays(-10));
        pool.ClosedAt = Now.AddDays(-1);
        pool.ClosedByEmail = "organizer@example.test";
        await db.SaveChangesAsync();

        var (service, mail) = NewReminder(db, new FixedClock(Now));

        Assert.Equal(1, await service.RemindAsync(EventId));
        Assert.Contains("pool closed", Assert.Single(mail.Messages).Html);
    }

    /// <summary>Nothing due ⇒ nothing sent. The §302 rule, tested rather than assumed.</summary>
    [Fact]
    public async Task Nothing_to_chase_sends_no_mail()
    {
        using var db = NewDb();
        await SeedPoolAsync(db, createdAt: Now.AddDays(-3), invoiceNumber: "20147");
        var (service, mail) = NewReminder(db, new FixedClock(Now));

        Assert.Equal(0, await service.RemindAsync(EventId));
        Assert.Empty(mail.Sent);
    }
}
