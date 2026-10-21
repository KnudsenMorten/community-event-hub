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
/// §1362 — reducing a prepaid pool with a correction backed by a credit note. Operator 2026-10-20: *"i need to reduce
/// coupons for apento by -9 as it was a mistake … i will prepare creditnote in erp, but can i reduce the coupons
/// available somewhere"*. FAKE names only.
/// </summary>
public sealed class CouponPoolReductionTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void A_lower_total_becomes_a_negative_correction()
    {
        var plan = CouponPoolReduction.For(purchased: 28, claimed: 0, newTotal: 19);
        Assert.True(plan.Ok);
        Assert.Equal(-9, plan.Delta);
    }

    [Theory]
    [InlineData(28, 0, 28)]    // not lower
    [InlineData(28, 0, 30)]    // an increase — that is the other box
    [InlineData(28, 0, -1)]    // nonsense
    [InlineData(28, 21, 19)]   // below what attendees already hold
    public void A_total_that_is_not_lower_or_is_below_the_claimed_tickets_is_refused(int purchased, int claimed, int newTotal)
    {
        var plan = CouponPoolReduction.For(purchased, claimed, newTotal);
        Assert.False(plan.Ok);
        Assert.Equal(0, plan.Delta);
        Assert.False(string.IsNullOrWhiteSpace(plan.Refusal));
    }

    [Fact]
    public void Reducing_to_exactly_the_claimed_tickets_is_allowed()
    {
        var plan = CouponPoolReduction.For(purchased: 28, claimed: 19, newTotal: 19);
        Assert.True(plan.Ok);
        Assert.Equal(-9, plan.Delta);
    }

    [Fact]
    public void The_correction_is_valued_at_the_latest_priced_purchase()
    {
        var purchases = new[]
        {
            new CouponPrepaidPurchase { Id = 1, Quantity = 17, UnitPriceDkk = 2800m, CreatedAt = Now.AddDays(-30) },
            new CouponPrepaidPurchase { Id = 2, Quantity = 11, UnitPriceDkk = 3000m, CreatedAt = Now.AddDays(-2) },
            new CouponPrepaidPurchase { Id = 3, Quantity = 5, UnitPriceDkk = null, CreatedAt = Now.AddDays(-1) },
        };
        Assert.Equal(3000m, CouponPoolReduction.UnitPriceFor(purchases));
        Assert.Null(CouponPoolReduction.UnitPriceFor(new[] { new CouponPrepaidPurchase { Quantity = 5 } }));
    }

    /// <summary>The chase names a correction as waiting for a CREDIT NOTE, not an invoice for minus nine tickets.</summary>
    [Fact]
    public async Task An_uncredited_correction_is_chased_as_a_credit_note()
    {
        using var db = new CommunityHubDbContext(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"coupon-reduce-{Guid.NewGuid():N}").Options);
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CR27", CommunityName = "C", DisplayName = "Coupon reduce",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        var rule = new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "PARTNER-2DAY",
            BillingType = CouponBillingType.AllocatedPrepaymentByCustomer, ErpCustomerNumber = 4242,
        };
        db.CouponInvoicingSettings.Add(rule);
        await db.SaveChangesAsync();
        db.CouponPrepaidAllocations.Add(new CouponPrepaidAllocation
        {
            EventId = EventId, CouponInvoicingSettingId = rule.Id, TicketClassId = "2d", TicketClassLabel = "2-day",
            CreatedAt = Now.AddDays(-10),
            Purchases = new List<CouponPrepaidPurchase>
            {
                new() { Quantity = 28, CreatedAt = Now.AddDays(-10), ErpInvoiceNumber = "198" },
                new() { Quantity = -9, CreatedAt = Now.AddDays(-2) },
            },
        });
        await db.SaveChangesAsync();

        var clock = new FixedClock(Now);
        var mail = new CapturingEmailSender();
        var alerts = new EngineAlertSender(mail, new EmailContextAccessor(), clock, NullLogger<EngineAlertSender>.Instance);

        Assert.Equal(1, await new CouponPrepaidBillingReminderService(db, alerts, clock).RemindAsync(EventId));

        var html = Assert.Single(mail.Messages).Html;
        Assert.Contains("-9 × 2-day", html);
        Assert.Contains("credit-note number", html);
    }
}
