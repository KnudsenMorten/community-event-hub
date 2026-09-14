using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1077 — volume-package qualification: three checks, one UNION, ten or more.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11, stating the requirement that decides the architecture: <i>"count of
/// attendees must validate so an emai is only counted once"</i> — a company buying 20 tickets on a
/// coupon is found by the order check AND the coupon check.</para>
///
/// <para>🔴 The double-count test below is the one that must never be deleted. If the implementation
/// is ever "optimised" into three counts added together, it is the only test that notices.</para>
/// </remarks>
public sealed class VolumePackageQualificationTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vp-{Guid.NewGuid():N}").Options);

    private static async Task<CommunityHubDbContext> SeedAsync(
        VolumePackageCompany company,
        (string Email, string? OrderId)[] attendees,
        (string OrderId, string BuyerEmail)[] orders)
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "D", IsActive = true,
        });
        company.EventId = EventId;
        db.VolumePackageCompanies.Add(company);

        foreach (var (email, orderId) in attendees)
        {
            db.Attendees.Add(new Attendee
            {
                EventId = EventId, Email = email, OrderId = orderId ?? string.Empty,
                BackstageTicketId = Guid.NewGuid().ToString("N"),
                MirrorState = MirrorState.Active,
            });
        }

        foreach (var (orderId, buyer) in orders)
        {
            db.Orders.Add(new Order
            {
                EventId = EventId, BackstageOrderId = orderId, BuyerEmail = buyer,
                MirrorState = MirrorState.Active,
            });
        }

        await db.SaveChangesAsync();
        return db;
    }

    private static (string Email, string? OrderId)[] People(int count, string domain, string? orderId = null) =>
        Enumerable.Range(1, count).Select(i => ($"p{i}@{domain}", orderId)).ToArray();

    /// <summary>Check 3 — the freelance-consultant case: ten people sharing a work domain.</summary>
    [Fact]
    public async Task Ten_attendees_on_the_company_domain_qualify()
    {
        using var db = await SeedAsync(
            new VolumePackageCompany { CustomName = "Fabrikam", Domains = "fabrikam.example" },
            People(10, "fabrikam.example"),
            []);

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(10, r.Count);
        Assert.True(r.Qualifies);
        Assert.Equal(10, r.FromAttendeeDomains);
    }

    /// <summary>⚠️ NINE does not qualify — the boundary, asserted so "≥10" cannot drift to ">9".</summary>
    [Fact]
    public async Task Nine_attendees_do_not_qualify()
    {
        using var db = await SeedAsync(
            new VolumePackageCompany { CustomName = "Fabrikam", Domains = "fabrikam.example" },
            People(9, "fabrikam.example"),
            []);

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(9, r.Count);
        Assert.False(r.Qualifies);
    }

    /// <summary>
    /// Check 1 — the buyer's domain is ours, so EVERY attendee on that order counts <i>"no matter the
    /// attendees email addresses used in the order"</i>. Here nobody shares the company domain.
    /// </summary>
    [Fact]
    public async Task An_order_bought_by_the_company_counts_every_attendee_on_it()
    {
        using var db = await SeedAsync(
            new VolumePackageCompany { CustomName = "Arrow", Domains = "arrow.com" },
            People(12, "gmail.com", orderId: "O-1"),
            [("O-1", "buyer@arrow.com")]);

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(12, r.Count);
        Assert.Equal(12, r.FromOrders);
        Assert.Equal(0, r.FromAttendeeDomains);
        Assert.True(r.Qualifies);
    }

    /// <summary>
    /// 🔴 THE TEST THE WHOLE DESIGN EXISTS FOR. The same twelve people are found by check 1 (their
    /// order was bought by the company) AND check 3 (they use the company domain). The answer must be
    /// TWELVE, not twenty-four.
    /// </summary>
    [Fact]
    public async Task A_person_found_by_two_checks_is_counted_ONCE()
    {
        using var db = await SeedAsync(
            new VolumePackageCompany { CustomName = "Arrow", Domains = "arrow.com" },
            People(12, "arrow.com", orderId: "O-1"),
            [("O-1", "buyer@arrow.com")]);

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(12, r.Count);                    // NOT 24
        Assert.Equal(12, r.FromOrders);               // each check still reports its own view…
        Assert.Equal(12, r.FromAttendeeDomains);      // …and they overlap completely
    }

    /// <summary>
    /// His case (b): several DOMAINS under one mother, counted together. Six + five = eleven, and
    /// neither domain would qualify alone — which is the point of linking them.
    /// </summary>
    [Fact]
    public async Task Two_domains_on_one_entity_are_counted_together()
    {
        using var db = await SeedAsync(
            new VolumePackageCompany { CustomName = "ARROW", Domains = "arrow.dk\narrow.no" },
            [.. People(6, "arrow.dk"), .. People(5, "arrow.no")],
            []);

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(11, r.Count);
        Assert.True(r.Qualifies);
    }

    /// <summary>
    /// The freelancer on a private address, linked by hand. ⚠️ Nine on the domain plus one linked =
    /// ten: the linked address is what tips it, so this asserts the link actually counts rather than
    /// merely being stored.
    /// </summary>
    [Fact]
    public async Task A_manually_linked_email_counts_toward_the_total()
    {
        using var db = await SeedAsync(
            new VolumePackageCompany
            {
                CustomName = "Fabrikam",
                Domains = "fabrikam.example",
                LinkedEmails = "freelancer@private-mail.example",
            },
            [.. People(9, "fabrikam.example"), ("freelancer@private-mail.example", null)],
            []);

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(10, r.Count);
        Assert.True(r.Qualifies);
    }

    /// <summary>
    /// 🔒 A CANCELLED ticket must drop the company back below the line — the reason the operator
    /// asked for a daily re-check: <i>"if fx an attendee cancels his ticket and company move from 10
    /// to 9, then they dont qualify"</i>.
    /// </summary>
    [Fact]
    public async Task A_cancelled_attendee_no_longer_counts()
    {
        using var db = await SeedAsync(
            new VolumePackageCompany { CustomName = "Fabrikam", Domains = "fabrikam.example" },
            People(10, "fabrikam.example"),
            []);

        var one = await db.Attendees.FirstAsync();
        one.MirrorState = MirrorState.Cancelled;
        await db.SaveChangesAsync();

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(9, r.Count);
        Assert.False(r.Qualifies);
    }

    /// <summary>
    /// ⚠️ Domains are matched case-insensitively and tolerate a stored "@domain" — an organizer
    /// pasting an address fragment must not silently produce a company that never qualifies.
    /// </summary>
    [Fact]
    public async Task Domain_matching_ignores_case_and_a_stray_at_sign()
    {
        using var db = await SeedAsync(
            new VolumePackageCompany { CustomName = "Fabrikam", Domains = "@Fabrikam.EXAMPLE" },
            People(10, "fabrikam.example"),
            []);

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(10, r.Count);
        Assert.True(r.Qualifies);
    }

    /// <summary>🔒 Another company's people never leak into this one's count.</summary>
    [Fact]
    public async Task Attendees_of_another_domain_are_not_counted()
    {
        using var db = await SeedAsync(
            new VolumePackageCompany { CustomName = "Fabrikam", Domains = "fabrikam.example" },
            [.. People(4, "fabrikam.example"), .. People(20, "someoneelse.com")],
            []);

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(4, r.Count);
        Assert.False(r.Qualifies);
    }

    // ── check 2 — the coupon, resolved through the ERP customer already curated for invoicing ────

    /// <summary>Order JSON in the shape <c>CouponClaimExtractor</c> reads (§787).</summary>
    private static string OrderJson(string orderId, string? orderPromo, params (string Email, string? Promo)[] tickets)
    {
        var t = string.Join(",", tickets.Select(x =>
            $$"""
              { "id":"T-{{x.Email.GetHashCode():X}}", "promo_code":{{(x.Promo is null ? "null" : $"\"{x.Promo}\"")}},
                "contact": { "email":"{{x.Email}}", "first_name":"Ann", "last_name":"Nielsen" } }
              """));
        var cost = orderPromo is null ? "{}" : $$"""{ "promo_code":"{{orderPromo}}" }""";
        return $$"""{ "id":"{{orderId}}", "cost": {{cost}}, "tickets": [ {{t}} ] }""";
    }

    /// <summary>
    /// 🔑 Check 2 the way the operator redirected it (§1077.2): <i>"you already have linked a erp
    /// customer to the coupon and you are allowed to fetch data in erp (no problem)"</i>. The entity
    /// declares an ERP customer number; the coupon is found through <c>CouponInvoicingSetting</c>,
    /// which is already curated for invoicing rather than typed a second time here.
    /// ⚠️ Nobody in this test shares the company domain and no order is bought by the company — so
    /// the ten can ONLY have come from the coupon, which is what makes it a test of check 2 alone.
    /// </summary>
    [Fact]
    public async Task A_coupon_owned_by_the_companys_ERP_customer_counts_its_claimers()
    {
        var claimers = Enumerable.Range(1, 10).Select(i => ($"c{i}@gmail.com", (string?)"ARROW10")).ToArray();

        using var db = await SeedAsync(
            new VolumePackageCompany
            {
                CustomName = "Arrow", Domains = "arrow.com", ErpCustomerNumbers = "5541",
            },
            claimers.Select(c => (c.Item1, (string?)"O-1")).ToArray(),
            []);

        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "ARROW10", ErpCustomerNumber = 5541,
        });
        db.Orders.Add(new Order
        {
            EventId = EventId, BackstageOrderId = "O-1", BuyerEmail = "someone@private-mail.example",
            MirrorState = MirrorState.Active, RawJson = OrderJson("O-1", null, claimers),
        });
        await db.SaveChangesAsync();

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(10, r.FromCoupons);
        Assert.Equal(0, r.FromOrders);
        Assert.Equal(0, r.FromAttendeeDomains);
        Assert.Equal(10, r.Count);
        Assert.True(r.Qualifies);
    }

    /// <summary>
    /// 🔒 A coupon belonging to a DIFFERENT ERP customer contributes nobody. ⚠️ Without this, the
    /// mapping could be joined the wrong way round — every coupon counting for every entity — and the
    /// happy-path test above would still pass, because it has only one entity to be wrong about.
    /// </summary>
    [Fact]
    public async Task A_coupon_belonging_to_another_customer_is_not_counted()
    {
        var claimers = Enumerable.Range(1, 10).Select(i => ($"c{i}@gmail.com", (string?)"OTHER10")).ToArray();

        using var db = await SeedAsync(
            new VolumePackageCompany
            {
                CustomName = "Arrow", Domains = "arrow.com", ErpCustomerNumbers = "5541",
            },
            claimers.Select(c => (c.Item1, (string?)"O-1")).ToArray(),
            []);

        db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
        {
            EventId = EventId, CouponName = "OTHER10", ErpCustomerNumber = 9999,
        });
        db.Orders.Add(new Order
        {
            EventId = EventId, BackstageOrderId = "O-1", BuyerEmail = "someone@private-mail.example",
            MirrorState = MirrorState.Active, RawJson = OrderJson("O-1", null, claimers),
        });
        await db.SaveChangesAsync();

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(0, r.FromCoupons);
        Assert.Equal(0, r.Count);
        Assert.False(r.Qualifies);
    }

    /// <summary>
    /// A coupon with NO ERP customer behind it — the override the design kept on the row itself
    /// (<c>CouponCodes</c>). ⚠️ It is the escape hatch for a coupon that was never invoiced through
    /// the finance system, and if it silently did nothing an organizer would type it and wait.
    /// </summary>
    [Fact]
    public async Task A_coupon_code_typed_on_the_row_counts_without_any_ERP_mapping()
    {
        var claimers = Enumerable.Range(1, 10).Select(i => ($"c{i}@gmail.com", (string?)"FREEBIE")).ToArray();

        using var db = await SeedAsync(
            new VolumePackageCompany
            {
                CustomName = "Arrow", Domains = "arrow.com", CouponCodes = "FREEBIE",
            },
            claimers.Select(c => (c.Item1, (string?)"O-1")).ToArray(),
            []);

        db.Orders.Add(new Order
        {
            EventId = EventId, BackstageOrderId = "O-1", BuyerEmail = "someone@private-mail.example",
            MirrorState = MirrorState.Active, RawJson = OrderJson("O-1", null, claimers),
        });
        await db.SaveChangesAsync();

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(10, r.FromCoupons);
        Assert.True(r.Qualifies);
    }

    /// <summary>
    /// 🔴 THE UNION ACROSS ALL THREE CHECKS, not just two. Ten people use the company domain (check
    /// 3), the same ten claimed the company's coupon (check 2), and the same ten sit on an order the
    /// company bought (check 1). ⚠️ Each check reports 10; the answer is <b>10</b>, not 30.
    /// </summary>
    [Fact]
    public async Task A_person_found_by_all_three_checks_is_still_counted_once()
    {
        var claimers = Enumerable.Range(1, 10).Select(i => ($"p{i}@arrow.com", (string?)"ARROW10")).ToArray();

        using var db = await SeedAsync(
            new VolumePackageCompany
            {
                CustomName = "Arrow", Domains = "arrow.com", CouponCodes = "ARROW10",
            },
            claimers.Select(c => (c.Item1, (string?)"O-1")).ToArray(),
            []);

        db.Orders.Add(new Order
        {
            EventId = EventId, BackstageOrderId = "O-1", BuyerEmail = "buyer@arrow.com",
            MirrorState = MirrorState.Active, RawJson = OrderJson("O-1", null, claimers),
        });
        await db.SaveChangesAsync();

        var r = (await new VolumePackageQualificationService(db).ComputeAllAsync(EventId)).Single();

        Assert.Equal(10, r.FromOrders);
        Assert.Equal(10, r.FromCoupons);
        Assert.Equal(10, r.FromAttendeeDomains);
        Assert.Equal(10, r.Count);      // NOT 30
    }
}
