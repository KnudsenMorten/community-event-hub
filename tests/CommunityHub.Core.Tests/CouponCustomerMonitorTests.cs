using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1093 — one monitor link per BILLING CUSTOMER, created automatically.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-19: *"i need also you to automaitcally set up monitor for every coupons so
/// they get a secure link and can see any usage / sign-ups"* … *"it could be great that the monitor
/// is linked per billing customer, so arrow gets one link that shows any usage for any coupons they
/// have"*.</para>
///
/// <para>🔑 Per customer, not per coupon: a partner holding three codes wants one link that answers
/// "my usage", and the billing customer is exactly who the invoice is addressed to — so the page and
/// the bill describe the same population.</para>
/// </remarks>
public sealed class CouponCustomerMonitorTests
{
    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "Test Community", DisplayName = "Test Community 2027",
            Code = "TC27", IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    /// <param name="issueLink">
    /// §1098 — the usage-link tick, OFF by default in the product (operator 2026-08-20: *"i prefer i
    /// set the tick on manually"*). These tests are ABOUT the link, so the helper ticks it on;
    /// the off case has its own tests below.
    /// </param>
    private static CouponInvoicingSetting Coupon(
        int eventId, string name, int? customer, bool issueLink = true) =>
        new()
        {
            EventId = eventId, CouponName = name,
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = customer,
            IssueUsageLink = issueLink,
        };

    // ---------------------------------------------------------------------
    //  Provisioning
    // ---------------------------------------------------------------------

    /// <summary>
    /// Three coupons, two customers ⇒ TWO links. The customer with two codes gets ONE, which is the
    /// whole point of choosing this grain over per-coupon.
    /// </summary>
    [Fact]
    public async Task One_link_per_customer_not_per_coupon()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        db.CouponInvoicingSettings.AddRange(
            Coupon(eventId, "ARROW-1DAY", 1234),
            Coupon(eventId, "ARROW-2DAY", 1234),
            Coupon(eventId, "GLOBE-2027", 5678));
        await db.SaveChangesAsync();

        var created = await new CouponCustomerMonitorProvisioner(db).EnsureAsync(eventId);

        Assert.Equal(2, created);
        var monitors = await db.AttendeeMonitors.ToListAsync();
        Assert.Equal(2, monitors.Count);
        Assert.All(monitors, m => Assert.Equal(AttendeeMonitorKind.ErpCustomer, m.Kind));
        Assert.Contains(monitors, m => m.Value == "1234");
        Assert.Contains(monitors, m => m.Value == "5678");
        // 🔒 Every link must be a real secret, not a predictable id.
        Assert.All(monitors, m => Assert.True(m.Token.Length >= 32, "token must be long and random"));
        Assert.Equal(2, monitors.Select(m => m.Token).Distinct().Count());
    }

    // ---------------------------------------------------------------------
    //  §1098 — the tick is off by default, and it decides everything
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔴 <b>OFF BY DEFAULT.</b> A coupon created without ticking the usage link gets no monitor —
    /// which is the whole point of the one-off invoice-sale category: *"they dont need to have a
    /// secure link as well"*.
    /// </summary>
    [Fact]
    public async Task A_coupon_without_the_tick_gets_no_link_at_all()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        db.CouponInvoicingSettings.Add(Coupon(eventId, "ONE-OFF", 1234, issueLink: false));
        await db.SaveChangesAsync();

        Assert.Equal(0, await new CouponCustomerMonitorProvisioner(db).EnsureAsync(eventId));
        Assert.Empty(await db.AttendeeMonitors.ToListAsync());
    }

    /// <summary>
    /// 🔑 <b>The unticked coupon is excluded by SCOPE, not by suppressing the whole link.</b> A
    /// customer holding both a normal coupon and a one-off still gets their link — it simply does
    /// not mention the one-off. Suppressing the link entirely would punish the partner's other
    /// agreement for the presence of a one-off sale.
    /// </summary>
    [Fact]
    public async Task An_unticked_coupon_is_left_out_of_a_link_the_customer_still_gets()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        db.CouponInvoicingSettings.AddRange(
            Coupon(eventId, "ARROW-NORMAL", 1234),                       // ticked
            Coupon(eventId, "ARROW-ONEOFF", 1234, issueLink: false));    // not

        db.Orders.AddRange(
            Order(eventId, "O-1", "ARROW-NORMAL", "ada@arrowpartner.test", "Ada", "One"),
            Order(eventId, "O-2", "ARROW-ONEOFF", "bob@arrowpartner.test", "Bob", "Two"));
        db.Attendees.AddRange(
            Attendee(eventId, "O-1", "ada@arrowpartner.test", "Ada", "One"),
            Attendee(eventId, "O-2", "bob@arrowpartner.test", "Bob", "Two"));
        await db.SaveChangesAsync();

        Assert.Equal(1, await new CouponCustomerMonitorProvisioner(db).EnsureAsync(eventId));
        var monitor = await db.AttendeeMonitors.SingleAsync();

        var rows = await new AttendeeMonitorQuery(db).RunAsync(monitor);

        Assert.Single(rows);
        Assert.Contains(rows, r => r.Email == "ada@arrowpartner.test");
        // 🔒 The one-off's claimant is not on the partner's page.
        Assert.DoesNotContain(rows, r => r.Email == "bob@arrowpartner.test");
    }

    /// <summary>A coupon with no customer has nobody to send a link to.</summary>
    [Fact]
    public async Task An_unmapped_coupon_gets_no_link()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        db.CouponInvoicingSettings.Add(Coupon(eventId, "MYSTERY", customer: null));
        await db.SaveChangesAsync();

        Assert.Equal(0, await new CouponCustomerMonitorProvisioner(db).EnsureAsync(eventId));
        Assert.Empty(await db.AttendeeMonitors.ToListAsync());
    }

    /// <summary>Running every few minutes must not pile up links.</summary>
    [Fact]
    public async Task Running_twice_creates_nothing_the_second_time()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);
        db.CouponInvoicingSettings.Add(Coupon(eventId, "ARROW-1DAY", 1234));
        await db.SaveChangesAsync();

        var provisioner = new CouponCustomerMonitorProvisioner(db);
        Assert.Equal(1, await provisioner.EnsureAsync(eventId));
        Assert.Equal(0, await provisioner.EnsureAsync(eventId));
        Assert.Single(await db.AttendeeMonitors.ToListAsync());
    }

    /// <summary>
    /// 🔴 <b>A REVOKED LINK MUST STAY REVOKED.</b> An organizer withdrawing a link is a deliberate
    /// act; a sweep that re-issues it on the next tick makes revocation impossible to keep — and it
    /// would do so silently, handing out a fresh URL to data somebody had decided to take back.
    /// </summary>
    [Fact]
    public async Task A_revoked_link_is_never_re_issued()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);
        db.CouponInvoicingSettings.Add(Coupon(eventId, "ARROW-1DAY", 1234));
        await db.SaveChangesAsync();

        var provisioner = new CouponCustomerMonitorProvisioner(db);
        await provisioner.EnsureAsync(eventId);

        var monitor = await db.AttendeeMonitors.SingleAsync();
        monitor.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        Assert.Equal(0, await provisioner.EnsureAsync(eventId));
        Assert.Single(await db.AttendeeMonitors.ToListAsync());
        Assert.NotNull((await db.AttendeeMonitors.SingleAsync()).RevokedAt);
    }

    /// <summary>The customer's real name is used when one can be resolved.</summary>
    [Fact]
    public async Task The_link_is_named_after_the_customer_when_a_name_is_available()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);
        db.CouponInvoicingSettings.Add(Coupon(eventId, "ARROW-1DAY", 1234));
        await db.SaveChangesAsync();

        await new CouponCustomerMonitorProvisioner(db).EnsureAsync(
            eventId, nameFor: (n, _) => Task.FromResult<string?>("Arrow ECS"));

        Assert.Contains("Arrow ECS", (await db.AttendeeMonitors.SingleAsync()).Name);
    }

    /// <summary>
    /// ⚠️ A name lookup that THROWS must not stop the link existing. A monitor called
    /// "customer 1234" is worth more than no monitor at all, and the resolver is an e-conomic call.
    /// </summary>
    [Fact]
    public async Task A_failing_name_lookup_still_produces_the_link()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);
        db.CouponInvoicingSettings.Add(Coupon(eventId, "ARROW-1DAY", 1234));
        await db.SaveChangesAsync();

        await new CouponCustomerMonitorProvisioner(db).EnsureAsync(
            eventId, nameFor: (_, _) => throw new InvalidOperationException("e-conomic is down"));

        Assert.Contains("1234", (await db.AttendeeMonitors.SingleAsync()).Name);
    }

    // ---------------------------------------------------------------------
    //  The query behind the link
    // ---------------------------------------------------------------------

    /// <summary>
    /// 🔴 <b>THE SCOPE TEST.</b> A customer's link shows attendees from EVERY coupon billed to them
    /// and from NO OTHER customer's coupon. This is the one failure this feature must not have — a
    /// forwarded link that widens into somebody else's attendee list.
    /// </summary>
    [Fact]
    public async Task The_link_shows_every_coupon_of_that_customer_and_nobody_elses()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        db.CouponInvoicingSettings.AddRange(
            Coupon(eventId, "ARROW-1DAY", 1234),
            Coupon(eventId, "ARROW-2DAY", 1234),
            Coupon(eventId, "GLOBE-2027", 5678));

        // One order per coupon, each with its own attendee.
        db.Orders.AddRange(
            Order(eventId, "O-1", "ARROW-1DAY", "ada@arrowpartner.test", "Ada", "One"),
            Order(eventId, "O-2", "ARROW-2DAY", "bob@arrowpartner.test", "Bob", "Two"),
            Order(eventId, "O-3", "GLOBE-2027", "eve@globe.test", "Eve", "Three"));

        db.Attendees.AddRange(
            Attendee(eventId, "O-1", "ada@arrowpartner.test", "Ada", "One"),
            Attendee(eventId, "O-2", "bob@arrowpartner.test", "Bob", "Two"),
            Attendee(eventId, "O-3", "eve@globe.test", "Eve", "Three"));
        await db.SaveChangesAsync();

        await new CouponCustomerMonitorProvisioner(db).EnsureAsync(eventId);
        var arrow = await db.AttendeeMonitors.SingleAsync(m => m.Value == "1234");

        var rows = await new AttendeeMonitorQuery(db).RunAsync(arrow);

        Assert.Equal(2, rows.Count);                                  // both Arrow codes
        Assert.Contains(rows, r => r.Email == "ada@arrowpartner.test");
        Assert.Contains(rows, r => r.Email == "bob@arrowpartner.test");
        Assert.DoesNotContain(rows, r => r.Email == "eve@globe.test"); // 🔒 never another customer's
    }

    /// <summary>
    /// A customer whose coupons exist but have no claims shows an EMPTY list — never everything.
    /// An empty scope that falls through to an unfiltered query is how a scoped page becomes a full
    /// attendee dump.
    /// </summary>
    [Fact]
    public async Task A_customer_with_no_claims_sees_nothing_not_everything()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        db.CouponInvoicingSettings.Add(Coupon(eventId, "ARROW-1DAY", 1234));
        db.Orders.Add(Order(eventId, "O-3", "GLOBE-2027", "eve@globe.test", "Eve", "Three"));
        db.Attendees.Add(Attendee(eventId, "O-3", "eve@globe.test", "Eve", "Three"));
        await db.SaveChangesAsync();

        await new CouponCustomerMonitorProvisioner(db).EnsureAsync(eventId);
        var arrow = await db.AttendeeMonitors.SingleAsync(m => m.Value == "1234");

        Assert.Empty(await new AttendeeMonitorQuery(db).RunAsync(arrow));
    }

    /// <summary>A monitor pointing at a customer with no coupons at all resolves to no rows.</summary>
    [Fact]
    public async Task A_customer_with_no_coupons_resolves_to_no_rows()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        db.Orders.Add(Order(eventId, "O-3", "GLOBE-2027", "eve@globe.test", "Eve", "Three"));
        db.Attendees.Add(Attendee(eventId, "O-3", "eve@globe.test", "Eve", "Three"));
        db.AttendeeMonitors.Add(new AttendeeMonitor
        {
            EventId = eventId, Name = "orphan", Kind = AttendeeMonitorKind.ErpCustomer,
            Value = "9999", Token = AttendeeMonitor.NewToken(),
        });
        await db.SaveChangesAsync();

        var orphan = await db.AttendeeMonitors.SingleAsync();
        Assert.Empty(await new AttendeeMonitorQuery(db).RunAsync(orphan));
    }

    /// <summary>
    /// 🔑 <b>A coupon added LATER appears through the link the partner already has.</b> The coupon
    /// set is resolved at read time, never frozen onto the monitor row — otherwise every new code
    /// would be silently missing from the page the partner uses to check their invoice.
    /// </summary>
    [Fact]
    public async Task A_coupon_added_later_appears_through_the_existing_link()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        db.CouponInvoicingSettings.Add(Coupon(eventId, "ARROW-1DAY", 1234));
        db.Orders.Add(Order(eventId, "O-1", "ARROW-1DAY", "ada@arrowpartner.test", "Ada", "One"));
        db.Attendees.Add(Attendee(eventId, "O-1", "ada@arrowpartner.test", "Ada", "One"));
        await db.SaveChangesAsync();

        await new CouponCustomerMonitorProvisioner(db).EnsureAsync(eventId);
        var arrow = await db.AttendeeMonitors.SingleAsync(m => m.Value == "1234");
        var query = new AttendeeMonitorQuery(db);
        Assert.Single(await query.RunAsync(arrow));

        // A new code for the same customer, and a claim on it. No new monitor, no new link.
        db.CouponInvoicingSettings.Add(Coupon(eventId, "ARROW-LATE", 1234));
        db.Orders.Add(Order(eventId, "O-9", "ARROW-LATE", "zoe@arrowpartner.test", "Zoe", "Nine"));
        db.Attendees.Add(Attendee(eventId, "O-9", "zoe@arrowpartner.test", "Zoe", "Nine"));
        await db.SaveChangesAsync();

        var rows = await query.RunAsync(arrow);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Email == "zoe@arrowpartner.test");
    }

    // ---------------------------------------------------------------------

    private static Order Order(
        int eventId, string orderId, string coupon, string email, string first, string last) =>
        new()
        {
            EventId = eventId,
            BackstageOrderId = orderId,
            MirrorState = MirrorState.Active,
            SourceCreatedAt = DateTimeOffset.UtcNow,
            BuyerName = "Buyer",
            BuyerEmail = "buyer@example.test",
            // 🔑 The REAL Zoho order shape, not an invented one: a root `id`, and the attendee in a
            // nested `contact` object. My first version put the e-mail on the ticket itself and
            // omitted the root id, so CouponClaimExtractor parsed zero claims and the scope test
            // passed vacuously — an empty result looks identical to correct scoping.
            RawJson =
                "{\"id\":\"" + orderId + "\",\"tickets\":[{"
                + "\"id\":\"T-" + orderId + "\",\"promo_code\":\"" + coupon + "\","
                + "\"contact\":{\"email\":\"" + email + "\",\"first_name\":\"" + first
                + "\",\"last_name\":\"" + last + "\"},"
                + "\"ticket_name\":\"2-day\",\"ticket_class_id\":\"TC1\","
                + "\"base_price\":3500,\"status_string\":\"active\"}]}",
        };

    private static Attendee Attendee(
        int eventId, string orderId, string email, string first, string last) =>
        new()
        {
            EventId = eventId, OrderId = orderId, Email = email,
            FirstName = first, LastName = last, MirrorState = MirrorState.Active,
        };
}
