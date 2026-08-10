using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1040 — VOLUME PACKAGE MONITORS: who a shared link shows, and — more importantly — who it does not.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10: <i>"i want to be able to generate a secure link per entry (monitors),
/// which i can send to persons where they can follow who bought a ticket … cancelled tickets should
/// not be included - only active attendees"</i>.</para>
///
/// <para>🔴 <b>These rows go to someone OUTSIDE the hub, with no login.</b> So the tests that matter
/// most are the exclusions: a cancelled ticket, another company's attendee, a look-alike domain.
/// Showing one row too few is an annoyance; showing one row too many is a personal-data leak to a
/// third party.</para>
/// </remarks>
public sealed class AttendeeMonitorQueryTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Bought = new(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"monitors-{Guid.NewGuid():N}").Options);

    private static async Task SeedEventAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "ELDK", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private static Order Order(
        string id, DateTimeOffset? bought = null, MirrorState state = MirrorState.Active,
        string? rawJson = null) =>
        new()
        {
            EventId = EventId, BackstageOrderId = id, MirrorState = state,
            SourceCreatedAt = bought ?? Bought, RawJson = rawJson,
        };

    private static Attendee Person(
        string email, string orderId, string first = "Ann", string last = "Nielsen",
        MirrorState state = MirrorState.Active) =>
        new()
        {
            EventId = EventId, Email = email, FirstName = first, LastName = last,
            FullName = $"{first} {last}", OrderId = orderId, MirrorState = state,
        };

    private static AttendeeMonitor Monitor(AttendeeMonitorKind kind, string value) =>
        new()
        {
            EventId = EventId, Name = "Test monitor", Kind = kind,
            Value = AttendeeMonitor.NormaliseValue(kind, value),
            Token = AttendeeMonitor.NewToken(),
        };

    // ── the domain monitor ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_domain_monitor_lists_that_companys_attendees()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Orders.Add(Order("ORD-1"));
        db.Attendees.Add(Person("ann@globeteam.com", "ORD-1", "Ann", "Nielsen"));
        db.Attendees.Add(Person("bo@globeteam.com", "ORD-1", "Bo", "Hansen"));
        await db.SaveChangesAsync();

        var rows = await new AttendeeMonitorQuery(db)
            .RunAsync(Monitor(AttendeeMonitorKind.EmailDomain, "globeteam.com"));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("ORD-1", r.OrderId));
        Assert.All(rows, r => Assert.Equal(Bought, r.BoughtAt));
    }

    /// <summary>
    /// 🔴 THE LEAK THIS FEATURE MUST NOT HAVE. Matching on the bare domain would make a monitor for
    /// <c>day.com</c> also match <c>someone@monday.com</c> — another company's attendee, on a link
    /// shared outside the hub. The match is anchored on "@".
    /// </summary>
    [Fact]
    public async Task A_domain_monitor_never_matches_a_look_alike_domain()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Orders.Add(Order("ORD-1"));
        db.Attendees.Add(Person("someone@monday.com", "ORD-1"));
        db.Attendees.Add(Person("real@day.com", "ORD-1", "Real", "Person"));
        await db.SaveChangesAsync();

        var rows = await new AttendeeMonitorQuery(db)
            .RunAsync(Monitor(AttendeeMonitorKind.EmailDomain, "day.com"));

        Assert.Single(rows);
        Assert.Equal("real@day.com", rows[0].Email);
    }

    /// <summary>🔒 Operator: <i>"cancelled tickets should not be included"</i>.</summary>
    [Fact]
    public async Task A_cancelled_ticket_is_not_listed()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Orders.Add(Order("ORD-1"));
        db.Attendees.Add(Person("ann@globeteam.com", "ORD-1"));
        db.Attendees.Add(Person("gone@globeteam.com", "ORD-1", "Gone", "Away", MirrorState.Cancelled));
        await db.SaveChangesAsync();

        var rows = await new AttendeeMonitorQuery(db)
            .RunAsync(Monitor(AttendeeMonitorKind.EmailDomain, "globeteam.com"));

        Assert.Single(rows);
        Assert.Equal("ann@globeteam.com", rows[0].Email);
    }

    /// <summary>
    /// ⚠️ An attendee on a CANCELLED ORDER keeps its own Active state in the mirror, so the buy date
    /// is deliberately read only from an active order — the row shows no date rather than a date
    /// from an order that no longer stands.
    /// </summary>
    [Fact]
    public async Task A_cancelled_order_does_not_supply_a_buy_date()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Orders.Add(Order("ORD-DEAD", state: MirrorState.Cancelled));
        db.Attendees.Add(Person("ann@globeteam.com", "ORD-DEAD"));
        await db.SaveChangesAsync();

        var rows = await new AttendeeMonitorQuery(db)
            .RunAsync(Monitor(AttendeeMonitorKind.EmailDomain, "globeteam.com"));

        Assert.Single(rows);
        Assert.Null(rows[0].BoughtAt);
    }

    /// <summary>🔒 A monitor is scoped to its edition — company ids and domains recur every year.</summary>
    [Fact]
    public async Task A_monitor_never_reaches_another_edition()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Events.Add(new Event
        {
            Id = 2, Code = "ELDK28", CommunityName = "ELDK", DisplayName = "ELDK 2028",
            StartDate = new DateOnly(2028, 2, 9), EndDate = new DateOnly(2028, 2, 10),
        });
        db.Attendees.Add(new Attendee
        {
            EventId = 2, Email = "ann@globeteam.com", FirstName = "Ann", LastName = "Nielsen",
            FullName = "Ann Nielsen", OrderId = "ORD-9", MirrorState = MirrorState.Active,
        });
        await db.SaveChangesAsync();

        var rows = await new AttendeeMonitorQuery(db)
            .RunAsync(Monitor(AttendeeMonitorKind.EmailDomain, "globeteam.com"));

        Assert.Empty(rows);
    }

    [Theory]
    [InlineData("@Globeteam.com ")]
    [InlineData("*@globeteam.com")]
    [InlineData(" GLOBETEAM.COM")]
    public async Task However_the_domain_was_typed_it_matches(string typed)
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Orders.Add(Order("ORD-1"));
        db.Attendees.Add(Person("ann@globeteam.com", "ORD-1"));
        await db.SaveChangesAsync();

        var rows = await new AttendeeMonitorQuery(db)
            .RunAsync(Monitor(AttendeeMonitorKind.EmailDomain, typed));

        Assert.Single(rows);
    }

    // ── the coupon monitor ─────────────────────────────────────────────────────────────────────

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

    [Fact]
    public async Task A_coupon_monitor_lists_attendees_who_used_that_code()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Orders.Add(Order("ORD-1", rawJson: OrderJson(
            "ORD-1", null, ("ann@acme.com", "VOLUME10"), ("bo@acme.com", "SOMETHINGELSE"))));
        db.Attendees.Add(Person("ann@acme.com", "ORD-1", "Ann", "Nielsen"));
        db.Attendees.Add(Person("bo@acme.com", "ORD-1", "Bo", "Hansen"));
        await db.SaveChangesAsync();

        var rows = await new AttendeeMonitorQuery(db)
            .RunAsync(Monitor(AttendeeMonitorKind.CouponCode, "VOLUME10"));

        Assert.Single(rows);
        Assert.Equal("ann@acme.com", rows[0].Email);
    }

    /// <summary>
    /// 🔑 §787's subtlety, inherited by reusing <c>CouponClaimExtractor</c> rather than re-parsing:
    /// a ticket whose own promo code is BLANK falls back to the order-level code. A <c>??</c> only
    /// falls back on null, and would silently drop these tickets.
    /// </summary>
    [Fact]
    public async Task A_blank_ticket_code_falls_back_to_the_order_code()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Orders.Add(Order("ORD-1", rawJson: OrderJson(
            "ORD-1", "VOLUME10", ("ann@acme.com", ""), ("bo@acme.com", null))));
        db.Attendees.Add(Person("ann@acme.com", "ORD-1", "Ann", "Nielsen"));
        db.Attendees.Add(Person("bo@acme.com", "ORD-1", "Bo", "Hansen"));
        await db.SaveChangesAsync();

        var rows = await new AttendeeMonitorQuery(db)
            .RunAsync(Monitor(AttendeeMonitorKind.CouponCode, "VOLUME10"));

        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// 🔒 The raw order JSON is a snapshot from pull time; `MirrorState` is maintained by every sync
    /// since. A ticket cancelled AFTER that snapshot must not appear just because the JSON still
    /// names it — which is why the coupon path is cross-checked against the mirrored attendees.
    /// </summary>
    [Fact]
    public async Task A_coupon_ticket_cancelled_after_the_snapshot_is_not_listed()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        db.Orders.Add(Order("ORD-1", rawJson: OrderJson("ORD-1", "VOLUME10", ("gone@acme.com", "VOLUME10"))));
        db.Attendees.Add(Person("gone@acme.com", "ORD-1", "Gone", "Away", MirrorState.Cancelled));
        await db.SaveChangesAsync();

        var rows = await new AttendeeMonitorQuery(db)
            .RunAsync(Monitor(AttendeeMonitorKind.CouponCode, "VOLUME10"));

        Assert.Empty(rows);
    }

    // ── the token ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔒 The token IS the credential for an unauthenticated page. Not a GUID: a GUID is a
    /// uniqueness primitive, not a secrecy one.
    /// </summary>
    [Fact]
    public void Tokens_are_long_url_safe_and_unique()
    {
        var tokens = Enumerable.Range(0, 500).Select(_ => AttendeeMonitor.NewToken()).ToList();

        Assert.Equal(500, tokens.Distinct().Count());
        Assert.All(tokens, t =>
        {
            Assert.True(t.Length >= 40, $"token too short: {t.Length}");
            Assert.DoesNotContain('+', t);
            Assert.DoesNotContain('/', t);
            Assert.DoesNotContain('=', t);
        });
    }
}
