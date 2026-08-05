using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §787 — the pure halves of coupon invoicing: pulling claims out of the raw Zoho order JSON CEH
/// already mirrors, and composing the invoice lines.
/// </summary>
/// <remarks>
/// These are the parts that decide WHO gets billed and HOW MUCH, so they are tested against realistic
/// Backstage payload shapes rather than discovered against a partner's real invoice.
/// </remarks>
public sealed class CouponInvoicingTests
{
    // A realistic Backstage order: two coupon tickets on one order, plus one ticket with no coupon.
    private const string OrderJson = """
    {
      "id": "5551234",
      "created_time": "2026-06-01T10:11:12Z",
      "cost": { "promo_code": "PARTNER-2027" },
      "tickets": [
        {
          "id": "t-1",
          "promo_code": "PARTNER-2027",
          "ticket_name": "2-day (Pre-day + Main Event)",
          "base_price": 4995.00,
          "total": 0,
          "contact": { "email": "Anna@Example.Com ", "first_name": "Anna", "last_name": "Berg" }
        },
        {
          "id": "t-2",
          "ticket_name": "2-day (Pre-day + Main Event)",
          "base_price": "4995.00",
          "total": 0,
          "contact": { "email": "bo@example.com", "first_name": "Bo", "last_name": "Dahl" }
        },
        {
          "id": "t-3",
          "promo_code": "",
          "ticket_name": "1-day",
          "base_price": 2995,
          "contact": { "email": "paid@example.com", "first_name": "Paid", "last_name": "Buyer" }
        }
      ]
    }
    """;

    // -------------------------------------------------------------------
    //  Extraction
    // -------------------------------------------------------------------

    [Fact]
    public void Only_coupon_bearing_tickets_are_extracted()
    {
        var claims = CouponClaimExtractor.FromOrderJson(OrderJson);

        // All three tickets are coupon tickets: t-1 carries its own code, t-2 has none at all and
        // t-3 has a BLANK one — and both of the latter fall back to the order's cost.promo_code.
        // 🔒 That blank-falls-back-too rule is the retired script's (`if ($ticket.promo_code)` is
        // false for ""), and getting it wrong in C# would have silently dropped t-3 off the invoice.
        Assert.Equal(3, claims.Count);
        Assert.All(claims, c => Assert.Equal("PARTNER-2027", c.CouponName));
    }

    [Fact]
    public void A_ticket_falls_back_to_the_ORDER_level_promo_code()
    {
        // t-2 carries no promo_code of its own; the order's cost.promo_code applies. Both shapes
        // occur in the live feed, which is why the script checked both.
        var claims = CouponClaimExtractor.FromOrderJson(OrderJson);
        var t2 = claims.Single(c => c.TicketId == "t-2");

        Assert.Equal("PARTNER-2027", t2.CouponName);
    }

    [Fact]
    public void An_order_with_no_coupon_anywhere_yields_nothing()
    {
        var json = """
        { "id": "999", "tickets": [ { "id": "x", "ticket_name": "1-day", "base_price": 2995 } ] }
        """;

        Assert.Empty(CouponClaimExtractor.FromOrderJson(json));
    }

    [Fact]
    public void The_reference_is_coupon_then_ORDER_id_so_one_order_makes_ONE_invoice()
    {
        var claims = CouponClaimExtractor.FromOrderJson(OrderJson);

        // 🔒 Keyed on the ORDER, not the ticket. Several tickets bought on one order under one
        // coupon share a reference and land on ONE invoice with a line each. A per-ticket key would
        // re-invoice every multi-ticket order the retired script ever handled.
        Assert.All(claims, c => Assert.Equal("PARTNER-2027-5551234", c.Reference));
        Assert.Single(claims.Select(c => c.Reference).Distinct());
    }

    [Fact]
    public void The_price_comes_from_base_price_NOT_total()
    {
        var claims = CouponClaimExtractor.FromOrderJson(OrderJson);

        // `total` is what the BUYER paid after the coupon — 0 for a fully covered ticket. Invoicing
        // the partner 0.00 is the exact failure this job exists to prevent. Every ticket here has
        // total 0 or none at all, so any price below can only have come from base_price.
        Assert.Equal(4995.00m, claims.Single(c => c.TicketId == "t-1").UnitPriceDkk);
        Assert.Equal(4995.00m, claims.Single(c => c.TicketId == "t-2").UnitPriceDkk);
        Assert.Equal(2995.00m, claims.Single(c => c.TicketId == "t-3").UnitPriceDkk);
        Assert.All(claims, c => Assert.True(c.UnitPriceDkk > 0m));
    }

    [Fact]
    public void A_price_arriving_as_a_quoted_string_parses_invariantly()
    {
        // t-2's base_price is the STRING "4995.00". Under a Danish culture that would become 499500.
        var t2 = CouponClaimExtractor.FromOrderJson(OrderJson).Single(c => c.TicketId == "t-2");
        Assert.Equal(4995.00m, t2.UnitPriceDkk);
    }

    [Fact]
    public void Contact_fields_are_trimmed()
    {
        var t1 = CouponClaimExtractor.FromOrderJson(OrderJson).Single(c => c.TicketId == "t-1");
        Assert.Equal("Anna@Example.Com", t1.Email);   // trimmed, case preserved
        Assert.Equal("Anna", t1.FirstName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public void Malformed_or_empty_order_json_yields_nothing_rather_than_throwing(string? json)
    {
        // One bad order must never stop the sweep for every other order.
        Assert.Empty(CouponClaimExtractor.FromOrderJson(json));
    }

    // -------------------------------------------------------------------
    //  Line composition
    // -------------------------------------------------------------------

    private static CouponClaim Claim(string ticketId = "t-1", decimal price = 4995m) =>
        new("PARTNER-2027-5551234", "PARTNER-2027", "5551234", ticketId,
            "anna@example.com", "Anna", "Berg", "2-day (Pre-day + Main Event)", price);

    [Fact]
    public void A_ticket_line_names_the_class_attendee_email_coupon_and_both_zoho_ids()
    {
        var text = CouponInvoiceLineComposer.ComposeTicketDescription(Claim(), null);

        Assert.Equal(
            "Ticket Class: 2-day (Pre-day + Main Event)\r\n"
            + "Attendee: Anna Berg\r\n"
            + "Email: anna@example.com\r\n"
            + "Coupon: PARTNER-2027\r\n"
            + "Zoho OrderId: 5551234\r\n"
            + "Zoho TicketId: t-1",
            text);
    }

    [Fact]
    public void A_converted_line_appends_the_SAME_three_line_note_the_webshop_invoice_uses()
    {
        // ⚠️ §786.1(f)'s wording was specified for the WEBSHOP script. It is reused here so the same
        // company does not issue two different renderings of the same sentence. Flagged in §787.
        var note = CouponInvoiceLineComposer.ComposeConversionNote("EUR", 4995m, 670m);
        var text = CouponInvoiceLineComposer.ComposeTicketDescription(Claim(), note);

        Assert.Contains("Currency conversion applied:\r\nDKK -> EUR\r\nOriginal DKK 4995 -> EUR 670",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_line_is_quantity_one_and_there_is_NO_header_line()
    {
        // Unlike the webshop invoice, every coupon line is a billable ticket.
        var lines = CouponInvoiceLineComposer.Compose(
            new[] { Claim("t-1"), Claim("t-2") }, vatZoneNumber: 1, convert: dkk => (dkk, null));

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(1m, l.Quantity));
        Assert.All(lines, l => Assert.Equal("1000", l.ProductNumber));
        Assert.Equal(new[] { 1, 2 }, lines.Select(l => l.LineNumber));
    }

    [Fact]
    public void Coupon_lines_round_to_two_decimals_and_do_NOT_use_the_webshop_ceiling()
    {
        // 🔒 The retired coupon script rounded to 2dp; the webshop script used Ceiling. Unifying
        // them would change the amount on invoices he already issues.
        var lines = CouponInvoiceLineComposer.Compose(
            new[] { Claim(price: 1000m) }, vatZoneNumber: 2,
            convert: dkk => (dkk * 0.134123m, null));

        Assert.Equal(134.12m, lines[0].UnitNetPrice);
    }

    // -------------------------------------------------------------------
    //  The mapping rules
    // -------------------------------------------------------------------

    [Fact]
    public void An_unmapped_coupon_is_neither_invoiceable_nor_silent()
    {
        var s = new CouponInvoicingSetting { CouponName = "NEW-2027" };

        Assert.Equal(CouponBillingType.Unmapped, s.BillingType);   // the default does nothing
        Assert.False(s.IsInvoiceable);
        Assert.True(s.NeedsAttention);                             // ...but it is visible
    }

    [Fact]
    public void NoInvoicing_is_a_DECISION_not_a_gap()
    {
        // The distinction that matters on the page: "we decided this is free" must not sit in the
        // same bucket as "nobody has told us who pays".
        var s = new CouponInvoicingSetting { BillingType = CouponBillingType.NoInvoicing };

        Assert.False(s.IsInvoiceable);
        Assert.False(s.NeedsAttention);
    }

    [Fact]
    public void A_billable_coupon_without_a_customer_number_needs_attention_and_cannot_invoice()
    {
        var s = new CouponInvoicingSetting
        {
            BillingType = CouponBillingType.ClaimableAdHocPaymentByCustomer,
            ErpCustomerNumber = null,
        };
        Assert.False(s.IsInvoiceable);
        Assert.True(s.NeedsAttention);

        s.ErpCustomerNumber = 39208032;
        Assert.True(s.IsInvoiceable);
        Assert.False(s.NeedsAttention);
    }

    /// <summary>
    /// 🔴 §794 — <b>A PREPAID COUPON IS NEVER INVOICEABLE.</b> This assertion used to say the
    /// opposite for <c>AllocatedPrepaymentByCustomer</c>, and that was a LIVE defect held back only
    /// by <c>Invoicing:DryRun</c> being true.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: *"lets say arrow dk buys 50 prepaid coupons. we need to deduct
    /// when claimed so they know remaining"*. A prepaid partner has ALREADY PAID up front for an
    /// allocation, so invoicing them per claim charges them a SECOND time for tickets they already
    /// own. What they are owed is a BALANCE, not an invoice — which is also why *"partner must not
    /// get a credit note"* is satisfiable: a cancelled prepaid claim returns the allocation.</para>
    ///
    /// <para>🔒 It still NEEDS a customer number — that is who the allocation belongs to — so it
    /// still needs attention without one. What changed is that HAVING one no longer makes it
    /// billable.</para>
    /// </remarks>
    [Fact]
    public void A_prepaid_coupon_is_never_invoiceable_even_with_a_customer()
    {
        var s = new CouponInvoicingSetting
        {
            BillingType = CouponBillingType.AllocatedPrepaymentByCustomer,
            ErpCustomerNumber = null,
        };
        Assert.False(s.IsInvoiceable);
        Assert.True(s.IsPrepaid);
        Assert.True(s.NeedsAttention);   // no customer ⇒ nobody owns the allocation

        s.ErpCustomerNumber = 39208032;
        Assert.False(s.IsInvoiceable);   // 🔴 the fix: STILL not invoiceable
        Assert.True(s.IsPrepaid);
        Assert.False(s.NeedsAttention);
    }
}
