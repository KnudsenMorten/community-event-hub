using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §821 — the customer's PURCHASE-ORDER reference on the webshop invoice header.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: <i>"i am missing a critical information - purchase order from CM, which
/// must be added to the invoice"</i>. It lives on the Company Manager COMPANY as
/// <c>billing_reference</c>; the retired script read that key and only ever put it in its own report
/// object, so it never reached an invoice.</para>
///
/// <para>🔒 Asserted CHARACTER FOR CHARACTER against the screenshot of e-conomic's *Noter og
/// referencer* dialog he filled in by hand — the same standard §786.1 set for the rest of this
/// wording. A customer's finance department matches on the exact phrase, and the label went through
/// two spellings in one conversation (<c>PurchaseInfo:</c> → <c>PurchaseOrder Info:</c>), so the
/// wording is pinned rather than eyeballed.</para>
/// </remarks>
public sealed class WebshopInvoicePurchaseOrderTests
{
    private const string SubHeading = "Sponsorship";

    [Fact]
    public void The_header_matches_what_he_typed_into_e_conomic()
    {
        var text = WebshopInvoiceLineComposer.ComposeHeaderTextLine(SubHeading, "POCUG000347");

        // Sponsorship / (blank row) / PurchaseOrder Info: / POCUG000347
        Assert.Equal(
            "Sponsorship\r\n\r\nPurchaseOrder Info:\r\nPOCUG000347",
            text);
    }

    [Fact]
    public void The_blank_row_he_asked_for_is_really_there()
    {
        var lines = WebshopInvoiceLineComposer
            .ComposeHeaderTextLine(SubHeading, "POCUG000347")
            .Split("\r\n");

        // "1 extra row then PurchaseOrder Info: (new line) <info>" — the extra row is the blank one,
        // and it is what separates the standing sub-heading from the customer's own reference.
        Assert.Equal(new[] { "Sponsorship", "", "PurchaseOrder Info:", "POCUG000347" }, lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_reference_prints_no_label_at_all(string? reference)
    {
        // ⚠️ Not a bare "PurchaseOrder Info:" with nothing under it. On a customer's invoice that
        // reads as a PO that failed to load rather than one that was never given — a support call
        // about a fault that did not happen.
        Assert.Equal("Sponsorship", WebshopInvoiceLineComposer.ComposeHeaderTextLine(SubHeading, reference));
    }

    [Fact]
    public void A_reference_with_stray_spacing_is_trimmed_not_printed_raw()
    {
        Assert.Equal(
            "Sponsorship\r\n\r\nPurchaseOrder Info:\r\nPOCUG000347",
            WebshopInvoiceLineComposer.ComposeHeaderTextLine("  Sponsorship  ", "  POCUG000347 "));
    }

    [Fact]
    public void The_label_is_the_screenshot_spelling()
    {
        // He typed "PurchaseInfo:" in chat and "PurchaseOrder Info:" in e-conomic. The screenshot won.
        Assert.Equal("PurchaseOrder Info:", WebshopInvoiceLineComposer.PurchaseOrderLabel);
    }

    [Fact]
    public void The_line_break_is_the_one_e_conomic_already_round_trips()
    {
        // 🔒 CRLF, matching the retired script and every description this composer writes (§786.1).
        // An invisible character choice is not the place to improve on a working system.
        Assert.Contains("\r\n", WebshopInvoiceLineComposer.ComposeHeaderTextLine(SubHeading, "PO-1"));
        Assert.DoesNotContain(
            "\n\n",
            WebshopInvoiceLineComposer.ComposeHeaderTextLine(SubHeading, "PO-1").Replace("\r\n", "|"));
    }
}
