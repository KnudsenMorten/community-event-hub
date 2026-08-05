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
/// §796 — the low-balance alert: warn BEFORE a prepaid pool runs out.
/// </summary>
/// <remarks>
/// <para>§794.1(4), operator 2026-08-04: *"Warn before it runs out"* … *"build the low-balance
/// alert"*.</para>
///
/// <para>🔴 <b>CEH cannot stop a claim.</b> Backstage has no coupon API, so an exhausted pool leaves
/// the promo code working and the next person takes a ticket nobody paid for. These tests hold the
/// two halves that decide whether the warning arrives in time: what counts as low, and the throttle
/// rule that must not hide the moment it gets worse.</para>
/// </remarks>
public sealed class CouponPrepaidLowBalanceAlertTests
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

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"coupon-low-{Guid.NewGuid():N}").Options);

    private static (CouponPrepaidLowBalanceAlertService Service, CapturingEmailSender Mail)
        NewService(CommunityHubDbContext db, FixedClock clock)
    {
        var mail = new CapturingEmailSender();
        var alerts = new EngineAlertSender(
            mail, new EmailContextAccessor(), clock, NullLogger<EngineAlertSender>.Instance);
        return (new CouponPrepaidLowBalanceAlertService(db, alerts, clock), mail);
    }

    /// <summary>One order carrying <paramref name="claimed"/> live 2-day claims on ARROW-DK.</summary>
    private static string OrderJson(int claimed, int cancelled = 0)
    {
        var tickets = Enumerable.Range(1, claimed)
            .Select(i => $$"""
                { "id": "t-{{i}}", "promo_code": "ARROW-DK", "ticket_name": "2-day",
                  "ticket_class_id": "{{TwoDay}}", "base_price": 3500.00, "total": 0,
                  "contact": { "email": "a{{i}}@example.test", "first_name": "A", "last_name": "One" } }
                """)
            .Concat(Enumerable.Range(1, cancelled).Select(i => $$"""
                { "id": "x-{{i}}", "promo_code": "ARROW-DK", "ticket_name": "2-day",
                  "ticket_class_id": "{{TwoDay}}", "base_price": 3500.00, "total": 0,
                  "status_string": "cancelled",
                  "contact": { "email": "x{{i}}@example.test", "first_name": "X", "last_name": "Two" } }
                """));

        return $$"""
        { "id": "9001", "cost": { "promo_code": "ARROW-DK" }, "tickets": [ {{string.Join(",", tickets)}} ] }
        """;
    }

    private static async Task<CouponPrepaidAllocation> SeedAsync(
        CommunityHubDbContext db, int purchased, int claimed, int cancelled = 0,
        int? threshold = null,
        CouponBillingType billing = CouponBillingType.AllocatedPrepaymentByCustomer,
        DateTimeOffset? closedAt = null)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CL27", CommunityName = "C", DisplayName = "Coupon low",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.Orders.Add(new Order { EventId = EventId, RawJson = OrderJson(claimed, cancelled) });

        var rule = new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "ARROW-DK", BillingType = billing, ErpCustomerNumber = 4242,
        };
        db.CouponInvoicingSettings.Add(rule);
        await db.SaveChangesAsync();

        var pool = new CouponPrepaidAllocation
        {
            EventId = EventId, CouponInvoicingSettingId = rule.Id,
            TicketClassId = TwoDay, TicketClassLabel = "2-day",
            LowBalanceThreshold = threshold,
            ClosedAt = closedAt, CreatedAt = Now.AddDays(-30),
            // §798.4 — what was bought lives on the purchases; the pool is their sum.
            Purchases = new List<CouponPrepaidPurchase>
            {
                new()
                {
                    Quantity = purchased, CreatedAt = Now.AddDays(-30), ErpInvoiceNumber = "20147",
                },
            },
        };
        db.CouponPrepaidAllocations.Add(pool);
        await db.SaveChangesAsync();
        return pool;
    }

    // ---------------------------------------------------------------------
    //  What counts as low
    // ---------------------------------------------------------------------

    /// <summary>50 bought, 47 claimed ⇒ 3 left, under the default threshold of 5.</summary>
    [Fact]
    public async Task A_pool_at_or_below_its_threshold_is_warned_about()
    {
        using var db = NewDb();
        await SeedAsync(db, purchased: 50, claimed: 47);
        var (service, mail) = NewService(db, new FixedClock(Now));

        Assert.Equal(1, await service.AlertAsync(EventId));

        var sent = Assert.Single(mail.Messages);
        // §808 — an ISSUE goes to mok@, not the actionable mailbox. Operator 2026-08-04:
        // *"alert mails (issues) ... goes to mok@expertslive.dk - and notification emails about new
        // pending invoices goes to info@expertslive.dk"*. The draft-created notice is the one that
        // stays on info@, and its own test asserts that.
        Assert.Equal(EngineAlertSender.Recipient, sent.To);
        Assert.Contains("running low", sent.Subject);
        Assert.Contains("ARROW-DK", sent.Html);
        Assert.Contains("2-day", sent.Html);
        Assert.Contains(">3<", sent.Html);              // 3 left, in the balance cell
        Assert.Contains("47 claimed", sent.Html);

        // 🔴 The operational fact that makes this urgent belongs IN the mail.
        Assert.Contains("does not stop the promo code", sent.Html);
    }

    [Fact]
    public async Task A_healthy_pool_is_never_warned_about()
    {
        using var db = NewDb();
        await SeedAsync(db, purchased: 50, claimed: 10);
        var (service, mail) = NewService(db, new FixedClock(Now));

        Assert.Equal(0, await service.AlertAsync(EventId));
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// 🔴 "3 left" is a heads-up; "-2" is money already gone. The subject must not read the same.
    /// </summary>
    [Fact]
    public async Task An_oversubscribed_pool_is_named_as_its_own_kind_of_problem()
    {
        using var db = NewDb();
        await SeedAsync(db, purchased: 5, claimed: 7);
        var (service, mail) = NewService(db, new FixedClock(Now));

        Assert.Equal(1, await service.AlertAsync(EventId));

        var sent = Assert.Single(mail.Messages);
        Assert.Contains("OVERSUBSCRIBED", sent.Subject);
        Assert.DoesNotContain("running low", sent.Subject);
        Assert.Contains("claimed more tickets than they paid for", sent.Html);
        Assert.Contains("2 beyond what was paid for", sent.Html);
    }

    /// <summary>
    /// 🔒 §795.1 — a pool CLOSED BY HAND is a decision somebody made. Warning about it is §302's
    /// noise in its purest form.
    /// </summary>
    [Fact]
    public async Task A_pool_closed_by_hand_is_never_warned_about()
    {
        using var db = NewDb();
        await SeedAsync(db, purchased: 5, claimed: 5, closedAt: Now.AddDays(-1));
        var (service, mail) = NewService(db, new FixedClock(Now));

        Assert.Equal(0, await service.AlertAsync(EventId));
        Assert.Empty(mail.Sent);
    }

    /// <summary>A cancelled ticket returns to the pool, so it is not low any more (§794).</summary>
    [Fact]
    public async Task A_cancelled_claim_puts_the_pool_back_above_the_line()
    {
        using var db = NewDb();
        await SeedAsync(db, purchased: 10, claimed: 4, cancelled: 4);
        var (service, mail) = NewService(db, new FixedClock(Now));

        // 4 live claims of 10 ⇒ 6 left, above the default threshold of 5 — the four cancelled ones
        // are not spent.
        Assert.Equal(0, await service.AlertAsync(EventId));
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task The_pools_own_threshold_wins_over_the_default()
    {
        using var db = NewDb();
        await SeedAsync(db, purchased: 50, claimed: 47, threshold: 2);
        var (service, mail) = NewService(db, new FixedClock(Now));

        // 3 left, threshold 2 ⇒ not low yet.
        Assert.Equal(0, await service.AlertAsync(EventId));
        Assert.Empty(mail.Sent);
    }

    /// <summary>An allocation on a coupon that is no longer prepaid is not a pool anyone claims against.</summary>
    [Fact]
    public async Task A_pool_on_a_coupon_that_is_no_longer_prepaid_is_ignored()
    {
        using var db = NewDb();
        await SeedAsync(
            db, purchased: 5, claimed: 5,
            billing: CouponBillingType.ClaimableAdHocPaymentByCustomer);
        var (service, mail) = NewService(db, new FixedClock(Now));

        Assert.Equal(0, await service.AlertAsync(EventId));
        Assert.Empty(mail.Sent);
    }

    // ---------------------------------------------------------------------
    //  §796.2 — the throttle, and what breaks it
    // ---------------------------------------------------------------------

    [Fact]
    public async Task It_goes_quiet_for_a_day_and_then_repeats()
    {
        using var db = NewDb();
        await SeedAsync(db, purchased: 50, claimed: 47);
        var clock = new FixedClock(Now);
        var (service, mail) = NewService(db, clock);

        Assert.Equal(1, await service.AlertAsync(EventId));

        clock.Set(Now.AddHours(6));
        Assert.Equal(0, await service.AlertAsync(EventId));
        Assert.Single(mail.Messages);

        clock.Set(Now.AddHours(25));
        Assert.Equal(1, await service.AlertAsync(EventId));
        Assert.Equal(2, mail.Messages.Count);
    }

    /// <summary>
    /// 🔴 THE ONE THIS FEATURE TURNS ON. A pool alerted at "3 left" that is now "-2" re-alerts
    /// INSIDE the quiet period, because that is a different fact — and it is exactly the transition
    /// the alert exists to catch. A plain 24-hour suppression would have hidden it for a day.
    /// </summary>
    [Fact]
    public async Task Getting_worse_breaks_the_quiet_period()
    {
        using var db = NewDb();
        var pool = await SeedAsync(db, purchased: 50, claimed: 47);   // 3 left
        var clock = new FixedClock(Now);
        var (service, mail) = NewService(db, clock);

        Assert.Equal(1, await service.AlertAsync(EventId));
        Assert.Equal(3, (await db.CouponPrepaidAllocations.SingleAsync()).LastLowBalanceAlertRemaining);

        // Two more claims land an hour later: 52 of 50 ⇒ -2.
        var order = await db.Orders.SingleAsync();
        order.RawJson = OrderJson(claimed: 52);
        await db.SaveChangesAsync();

        clock.Set(Now.AddHours(1));
        Assert.Equal(1, await service.AlertAsync(EventId));

        Assert.Equal(2, mail.Messages.Count);
        Assert.Contains("OVERSUBSCRIBED", mail.Messages[1].Subject);
        Assert.Equal(-2, (await db.CouponPrepaidAllocations.SingleAsync()).LastLowBalanceAlertRemaining);
    }

    /// <summary>
    /// ⚠️ RECOVERY IS SILENT, deliberately. A cancellation putting a ticket back is good news, and
    /// a mail about good news is what teaches people to ignore the mail.
    /// </summary>
    [Fact]
    public async Task Getting_better_but_still_low_says_nothing_more()
    {
        using var db = NewDb();
        await SeedAsync(db, purchased: 50, claimed: 49);              // 1 left
        var clock = new FixedClock(Now);
        var (service, mail) = NewService(db, clock);

        Assert.Equal(1, await service.AlertAsync(EventId));

        // One is cancelled: 2 left. Still low, but BETTER.
        var order = await db.Orders.SingleAsync();
        order.RawJson = OrderJson(claimed: 48, cancelled: 1);
        await db.SaveChangesAsync();

        clock.Set(Now.AddHours(2));
        Assert.Equal(0, await service.AlertAsync(EventId));
        Assert.Single(mail.Messages);
    }

    /// <summary>Nothing low ⇒ nothing sent. The §302 rule, tested rather than assumed.</summary>
    [Fact]
    public async Task Nothing_low_sends_no_mail()
    {
        using var db = NewDb();
        await SeedAsync(db, purchased: 100, claimed: 1);
        var (service, mail) = NewService(db, new FixedClock(Now));

        Assert.Equal(0, await service.AlertAsync(EventId));
        Assert.Empty(mail.Sent);
    }

    /// <summary>An edition with no prepaid pools at all does nothing and says so in the log only.</summary>
    [Fact]
    public async Task No_pools_at_all_is_a_silent_no_op()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CL27", CommunityName = "C", DisplayName = "Coupon low",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();
        var (service, mail) = NewService(db, new FixedClock(Now));

        Assert.Equal(0, await service.AlertAsync(EventId));
        Assert.Empty(mail.Sent);
    }
}
