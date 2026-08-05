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
/// §797 — detecting coupons nobody has told CEH about, and saying so once.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"automatic rule creation detecting new coupons and their relevant
/// status. i need a reminder for that when new are detected"*.</para>
///
/// <para>🔴 <b>The case that made this necessary is the CANCELLED one.</b> Measured on PROD: every
/// coupon claim in the mirror was cancelled, so §787's sweep returned before its auto-create and the
/// coupon was invisible for ever — while the code itself still worked and the next claim on it would
/// have been billable.</para>
/// </remarks>
public sealed class CouponDiscoveryTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"coupon-disc-{Guid.NewGuid():N}").Options);

    private static (CouponDiscoveryService Service, CapturingEmailSender Mail) NewService(
        CommunityHubDbContext db)
    {
        var mail = new CapturingEmailSender();
        var clock = new FixedClock();
        var alerts = new EngineAlertSender(
            mail, new EmailContextAccessor(), clock, NullLogger<EngineAlertSender>.Instance);
        return (new CouponDiscoveryService(db, alerts, clock), mail);
    }

    /// <summary>Two coupons on one order: one live, one cancelled — the PROD shape.</summary>
    private const string OrderJson = """
    {
      "id": "9001",
      "tickets": [
        { "id": "t-1", "promo_code": "ARROW-DK", "ticket_name": "2-day",
          "ticket_class_id": "148800001", "base_price": 3500.00, "total": 0,
          "contact": { "email": "a@example.test", "first_name": "A", "last_name": "One" } },
        { "id": "t-2", "promo_code": "NORDIC-25", "ticket_name": "1-day",
          "ticket_class_id": "148800002", "base_price": 2000.00, "total": 0,
          "status_string": "cancelled",
          "contact": { "email": "b@example.test", "first_name": "B", "last_name": "Two" } }
      ]
    }
    """;

    private static async Task SeedAsync(CommunityHubDbContext db, string json = OrderJson)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CD27", CommunityName = "C", DisplayName = "Coupon discovery",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        db.Orders.Add(new Order { EventId = EventId, RawJson = json });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_unknown_coupon_gets_an_unmapped_rule_and_one_mail()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var (service, mail) = NewService(db);

        var found = await service.DiscoverAsync(EventId);

        Assert.Equal(2, found.Count);

        var rules = await db.CouponInvoicingSettings.OrderBy(c => c.CouponName).ToListAsync();
        Assert.Equal(2, rules.Count);
        // 🔒 Never guessed: "nobody has said who pays" is the honest status.
        Assert.All(rules, r => Assert.Equal(CouponBillingType.Unmapped, r.BillingType));
        Assert.All(rules, r => Assert.Null(r.ErpCustomerNumber));
        Assert.All(rules, r => Assert.Equal(Now, r.FirstSeenClaimedAt));

        var sent = Assert.Single(mail.Messages);
        // §808 — an ISSUE goes to mok@, not the actionable mailbox. Operator 2026-08-04:
        // *"alert mails (issues) ... goes to mok@expertslive.dk - and notification emails about new
        // pending invoices goes to info@expertslive.dk"*. The draft-created notice is the one that
        // stays on info@, and its own test asserts that.
        Assert.Equal(EngineAlertSender.Recipient, sent.To);
        Assert.Contains("2 new coupon code(s) detected", sent.Subject);
        Assert.Contains("ARROW-DK", sent.Html);
        Assert.Contains("NORDIC-25", sent.Html);
    }

    /// <summary>
    /// 🔴 THE ONE THIS SERVICE EXISTS FOR. A coupon whose only claim was cancelled is exactly what
    /// §787's sweep drops — and it still needs a decision, because the code keeps working.
    /// </summary>
    [Fact]
    public async Task A_coupon_whose_only_claims_are_cancelled_is_still_detected()
    {
        using var db = NewDb();
        await SeedAsync(db, """
        { "id": "9002", "tickets": [
            { "id": "x-1", "promo_code": "GHOST-CODE", "ticket_name": "2-day",
              "base_price": 3500.00, "status_string": "cancelled",
              "contact": { "email": "c@example.test", "first_name": "C", "last_name": "Three" } } ] }
        """);
        var (service, mail) = NewService(db);

        var found = Assert.Single(await service.DiscoverAsync(EventId));

        Assert.Equal("GHOST-CODE", found.CouponName);
        Assert.Equal(0, found.LiveClaims);
        Assert.Equal(1, found.CancelledClaims);
        Assert.Equal(0m, found.ValueDkk);           // nothing live is worth anything

        var html = Assert.Single(mail.Messages).Html;
        Assert.Contains("none live", html);
        // ⚠️ And it explains WHY this one was never picked up before.
        Assert.Contains("only cancelled claims", html);
        Assert.Contains("skips cancelled tickets", html);
    }

    [Fact]
    public async Task A_coupon_that_already_has_a_rule_is_not_reported_again()
    {
        using var db = NewDb();
        await SeedAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "ARROW-DK",
            BillingType = CouponBillingType.AllocatedPrepaymentByCustomer, ErpCustomerNumber = 4242,
        });
        await db.SaveChangesAsync();
        var (service, mail) = NewService(db);

        var found = Assert.Single(await service.DiscoverAsync(EventId));

        Assert.Equal("NORDIC-25", found.CouponName);
        Assert.DoesNotContain("ARROW-DK", Assert.Single(mail.Messages).Html);
        // 🔒 The mapped rule is untouched — discovery never rewrites a decision somebody made.
        var arrow = await db.CouponInvoicingSettings.SingleAsync(c => c.CouponName == "ARROW-DK");
        Assert.Equal(CouponBillingType.AllocatedPrepaymentByCustomer, arrow.BillingType);
        Assert.Equal(4242, arrow.ErpCustomerNumber);
    }

    /// <summary>
    /// ⚠️ A coupon typed with different case is the SAME coupon. A second row would trip the unique
    /// index — or split one partner's billing across two rules.
    /// </summary>
    [Fact]
    public async Task Matching_an_existing_rule_is_case_insensitive()
    {
        using var db = NewDb();
        await SeedAsync(db);
        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "arrow-dk", BillingType = CouponBillingType.NoInvoicing,
        });
        await db.SaveChangesAsync();
        var (service, _) = NewService(db);

        var found = await service.DiscoverAsync(EventId);

        Assert.DoesNotContain(found, d => d.CouponName.Equals("ARROW-DK", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, await db.CouponInvoicingSettings.CountAsync());   // one seeded + one new
    }

    /// <summary>Running twice in a row discovers nothing the second time, and mails once.</summary>
    [Fact]
    public async Task A_second_pass_is_silent()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var (service, mail) = NewService(db);

        Assert.Equal(2, (await service.DiscoverAsync(EventId)).Count);
        Assert.Empty(await service.DiscoverAsync(EventId));
        Assert.Single(mail.Messages);
    }

    /// <summary>
    /// §797.5 — the new rows are stamped as alerted, so §787's "still unmapped" chase does not send
    /// a second mail about the same coupon in the same hour. One event, one mail.
    /// </summary>
    [Fact]
    public async Task Discovery_stamps_the_rows_so_the_unmapped_chase_does_not_double_mail()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var (service, mail) = NewService(db);

        await service.DiscoverAsync(EventId);
        Assert.All(await db.CouponInvoicingSettings.ToListAsync(), r => Assert.Equal(Now, r.LastAlertedAt));

        var alerts = new CouponMappingAlertService(
            db,
            new EngineAlertSender(mail, new EmailContextAccessor(), new FixedClock(),
                NullLogger<EngineAlertSender>.Instance),
            new FixedClock());

        Assert.Equal(0, await alerts.AlertAsync(EventId, new[] { "ARROW-DK", "NORDIC-25" }));
        Assert.Single(mail.Messages);      // still just the discovery mail
    }

    /// <summary>Nothing new ⇒ no mail. The §302 rule.</summary>
    [Fact]
    public async Task No_claims_at_all_is_a_silent_no_op()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CD27", CommunityName = "C", DisplayName = "Coupon discovery",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();
        var (service, mail) = NewService(db);

        Assert.Empty(await service.DiscoverAsync(EventId));
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// ⚠️ The mail states the LIMIT of the feature: Backstage has no coupon API, so this is not the
    /// list of codes that exist — only the ones somebody has used. A list that looks complete and is
    /// not is worse than no list.
    /// </summary>
    [Fact]
    public async Task The_mail_says_it_can_only_see_coupons_that_have_been_used()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var (service, mail) = NewService(db);

        await service.DiscoverAsync(EventId);

        var html = Assert.Single(mail.Messages).Html;
        Assert.Contains("no API for listing promo codes", html);
        Assert.Contains("never claimed stays unknown", html);
    }

    /// <summary>What CEH knows travels with the coupon — that is the "relevant status" he asked for.</summary>
    [Fact]
    public async Task It_reports_what_it_knows_about_each_coupon()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var (service, mail) = NewService(db);

        var arrow = Assert.Single(await service.DiscoverAsync(EventId), d => d.CouponName == "ARROW-DK");
        Assert.Equal(1, arrow.LiveClaims);
        Assert.Equal(0, arrow.CancelledClaims);
        Assert.Equal(3500m, arrow.ValueDkk);
        Assert.Equal(new[] { "2-day" }, arrow.TicketClasses);

        var html = Assert.Single(mail.Messages).Html;
        Assert.Contains("3500 DKK", html);
        Assert.Contains("2-day", html);
        Assert.Contains("not mapped", html);
    }
}
