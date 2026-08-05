using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §811 — the draft payload must match the invoice the RETIRED SCRIPT produced, field for field.
/// </summary>
/// <remarks>
/// <para>Three defects reached a real customer invoice (draft 177) because §786's promise — *"the
/// invoice it produces is the invoice he already gets"* — was kept by assumption in places rather
/// than by reading one of the script's invoices. These facts are taken from **script invoice 165**
/// (Patch My PC) and **164** (System Center Dudes), read from the live API on 2026-08-04.</para>
/// </remarks>
public sealed class InvoicePayloadMatchesTheScriptTests
{
    private static EconomicCustomerDetail Customer(
        int? layoutNumber = null, int paymentTerms = 4) =>
        new(
            CustomerNumber: 39208032, Name: "2linkIT ApS", Address: "Vej 1", Zip: "2100",
            City: "København", Country: "Denmark", Currency: "DKK",
            VatZoneNumber: 1, VatZoneSelf: null,
            PaymentTermsNumber: paymentTerms,
            PaymentTermsSelf: $"https://restapi.e-conomic.com/payment-terms/{paymentTerms}",
            Self: null, Ean: null, AttentionContactNumber: 1,
            LayoutNumber: layoutNumber,
            LayoutSelf: layoutNumber is null ? null : $"https://restapi.e-conomic.com/layouts/{layoutNumber}");

    private static EconomicDraftInvoice Invoice(EconomicCustomerDetail customer) =>
        new(
            Customer: customer,
            Date: new DateOnly(2026, 8, 4),
            LayoutNumber: 21, LayoutSelf: "https://restapi.e-conomic.com/layouts/21",
            Currency: "DKK",
            OtherReference: "WebshopOrderId-10726",
            VendorEmployeeNumber: 1,
            AttentionContactNumber: 1, YourReferenceContactNumber: 1,
            Heading: "ELDK27 - Experts Live Denmark",
            TextLine1: "Sponsorship",
            Lines: new[] { new EconomicInvoiceLine(1, "Sponsor Webshop Order: 10726 (2026-05-26)") });

    /// <summary>
    /// 🔴 §811(a) — <b>payment terms are NOT sent at all.</b> Operator: *"you are overruling the
    /// default terms defined on customer"*. e-conomic applies the customer's own terms when the field
    /// is absent; echoing a copy back makes CEH the thing that decides them, and the copy goes stale
    /// the day he changes the customer.
    /// </summary>
    [Fact]
    public void The_payment_terms_sent_are_the_customers_own()
    {
        var payload = LiveEconomicInvoiceClient.BuildPayload(Invoice(Customer(paymentTerms: 4)));

        // 🔴 The field is REQUIRED by the draft schema — omitting it answers
        // 400 E00500 "Required properties are missing from object: paymentTerms" (measured).
        // "Not overruling" therefore means sending the CUSTOMER'S OWN value, read from their
        // e-conomic record on this run. CEH has no default of its own.
        var terms = Assert.IsType<Dictionary<string, object?>>(payload["paymentTerms"]);
        Assert.Equal(4, terms["paymentTermsNumber"]);
        Assert.Equal("https://restapi.e-conomic.com/payment-terms/4", terms["self"]);
    }

    /// <summary>
    /// §811(c) — BOTH employee references appear. *"ref is Morten Waltorp Knudsen, ref 2 is Martin
    /// Byskov"* … *"both must be mentioned"*. Probed against the live API: e-conomic accepts and
    /// stores both slots independently.
    /// </summary>
    [Fact]
    public void Both_employee_references_are_sent()
    {
        var invoice = Invoice(Customer()) with
        {
            VendorEmployeeNumber = 3, SalesPersonEmployeeNumber = 1,
        };

        var references = Assert.IsType<Dictionary<string, object?>>(
            LiveEconomicInvoiceClient.BuildPayload(invoice)["references"]);

        var vendor = Assert.IsType<Dictionary<string, object?>>(references["vendorReference"]);
        var sales = Assert.IsType<Dictionary<string, object?>>(references["salesPerson"]);
        Assert.Equal(3, vendor["employeeNumber"]);
        Assert.Equal(1, sales["employeeNumber"]);
    }

    /// <summary>🔒 Null omits the second slot entirely — the pre-§811 shape.</summary>
    [Fact]
    public void The_second_reference_is_omitted_when_not_configured()
    {
        var invoice = Invoice(Customer()) with { SalesPersonEmployeeNumber = null };

        var references = Assert.IsType<Dictionary<string, object?>>(
            LiveEconomicInvoiceClient.BuildPayload(invoice)["references"]);

        Assert.False(references.ContainsKey("salesPerson"));
    }

    /// <summary>
    /// 🔴 §811(d) — <b>the customer's OWN layout wins, and that is the invoice's language.</b>
    /// Measured: Patch My PC (USA) carries layout 23 = English, and the script's invoice for them is
    /// English. CEH used to force the configured Danish layout on everyone.
    /// </summary>
    [Fact]
    public void The_customers_own_layout_decides_the_language()
    {
        var payload = LiveEconomicInvoiceClient.BuildPayload(Invoice(Customer(layoutNumber: 23)));

        var layout = Assert.IsType<Dictionary<string, object?>>(payload["layout"]);
        Assert.Equal(23, layout["layoutNumber"]);
        Assert.Equal("https://restapi.e-conomic.com/layouts/23", layout["self"]);
    }

    /// <summary>A customer with no layout of their own falls back to the configured one (Danish).</summary>
    [Fact]
    public void A_customer_without_a_layout_falls_back_to_the_configured_one()
    {
        var payload = LiveEconomicInvoiceClient.BuildPayload(Invoice(Customer(layoutNumber: null)));

        var layout = Assert.IsType<Dictionary<string, object?>>(payload["layout"]);
        Assert.Equal(21, layout["layoutNumber"]);
    }

    /// <summary>
    /// §811(b) — the heading and the line under it are the script's two fields, which print as one
    /// phrase: *"ELDK27 - Experts Live Denmark Sponsorship"*. The port shipped "Webshop Order".
    /// </summary>
    [Fact]
    public void The_heading_and_subheading_match_the_script()
    {
        var payload = LiveEconomicInvoiceClient.BuildPayload(Invoice(Customer()));

        var notes = Assert.IsType<Dictionary<string, object?>>(payload["notes"]);
        Assert.Equal("ELDK27 - Experts Live Denmark", notes["heading"]);
        Assert.Equal("Sponsorship", notes["textLine1"]);
    }

    /// <summary>The shipped defaults are the script's values, so an unconfigured environment matches.</summary>
    [Fact]
    public void The_shipped_defaults_are_the_scripts_values()
    {
        var options = new EconomicErpOptions();

        Assert.Equal("ELDK27 - Experts Live Denmark", options.InvoiceHeading);
        Assert.Equal("Sponsorship", options.InvoiceTextLine1);
        Assert.Equal("Danish", options.InvoiceLayoutNameLike);   // §809.2 — "Dansk" matched nothing

        // 🔴 §811(c) — his CHANGE, not the script's value: *"our ref should be employee 3 - Morten
        // Waltorp Knudsen"*. The script's own invoices carry employee 1 (Martin Byskov), so this is
        // the one field that deliberately DIVERGES from "the invoice he already gets".
        Assert.Equal(3, options.InvoiceVendorEmployeeNumber);
    }

    /// <summary>"Our reference" reaches the payload as the configured employee.</summary>
    [Fact]
    public void Our_reference_is_the_configured_employee()
    {
        var invoice = Invoice(Customer()) with { VendorEmployeeNumber = 3 };

        var payload = LiveEconomicInvoiceClient.BuildPayload(invoice);
        var references = Assert.IsType<Dictionary<string, object?>>(payload["references"]);
        var vendor = Assert.IsType<Dictionary<string, object?>>(references["vendorReference"]);

        Assert.Equal(3, vendor["employeeNumber"]);
    }

    /// <summary>
    /// 🔒 Still sent, and still from the customer: the VAT zone. Unlike payment terms this is not a
    /// customer PREFERENCE — it decides the tax treatment of the lines, and the composer already
    /// keys the product number on it.
    /// </summary>
    [Fact]
    public void The_vat_zone_is_still_sent()
    {
        var payload = LiveEconomicInvoiceClient.BuildPayload(Invoice(Customer()));

        var recipient = Assert.IsType<Dictionary<string, object?>>(payload["recipient"]);
        var vatZone = Assert.IsType<Dictionary<string, object?>>(recipient["vatZone"]);
        Assert.Equal(1, vatZone["vatZoneNumber"]);
    }
}
