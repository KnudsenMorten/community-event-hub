using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §786.1 — the operator's six line-composition changes, asserted character for character.
/// </summary>
/// <remarks>
/// <para>He specified this part down to the line break, and pasted the exact output he wants. So it
/// is pinned literally rather than checked for "contains the right words": the whole point of (f) is
/// WHERE the newline falls, and a <c>Contains</c> assertion would pass on the very string he asked
/// to change.</para>
///
/// <para>⚠️ These tests prove CEH BUILDS the right string. They cannot prove e-conomic PRINTS it —
/// if its description field collapsed newlines, every test here would still pass while the invoice
/// looked exactly as wrong as before. That check belongs against a real draft
/// ([[verify-dont-assume]]), and is recorded as such in §786.1.</para>
/// </remarks>
public sealed class WebshopInvoiceLineComposerTests
{
    private static readonly DateTimeOffset OrderDate =
        new(2026, 8, 4, 14, 23, 11, TimeSpan.FromHours(2));

    // ---------------------------------------------------------------------
    //  (f) the currency note — his exact text
    // ---------------------------------------------------------------------

    [Fact]
    public void The_currency_note_is_EXACTLY_the_three_lines_he_asked_for()
    {
        var note = WebshopInvoiceLineComposer.ComposeConversionNote("EUR", "USD", 13500m, 15329m);

        // Pasted from his message, verbatim.
        var expected =
            "Currency conversion applied:\r\n" +
            "EUR -> USD\r\n" +
            "Original EUR 13500 -> USD 15329";

        Assert.Equal(expected, note);
    }

    [Fact]
    public void The_label_no_longer_shares_a_line_with_the_currency_pair()
    {
        // This is the ENTIRE change in (f), and it is the one a "contains" assertion would miss:
        // the old text read "Currency conversion applied: EUR -> USD" on one line.
        var note = WebshopInvoiceLineComposer.ComposeConversionNote("EUR", "USD", 13500m, 15329m);

        Assert.DoesNotContain("applied: EUR", note, StringComparison.Ordinal);
        Assert.StartsWith("Currency conversion applied:\r\n", note, StringComparison.Ordinal);

        var lines = note.Split("\r\n");
        Assert.Equal(3, lines.Length);
        Assert.Equal("Currency conversion applied:", lines[0]);
        Assert.Equal("EUR -> USD", lines[1]);
    }

    [Theory]
    [InlineData("eur", "usd", "EUR -> USD")]
    [InlineData(" EUR ", "Usd", "EUR -> USD")]
    public void Currency_codes_are_normalised_to_upper_case(string from, string to, string expectedPair)
    {
        var note = WebshopInvoiceLineComposer.ComposeConversionNote(from, to, 100m, 110m);
        Assert.Equal(expectedPair, note.Split("\r\n")[1]);
    }

    [Fact]
    public void A_whole_amount_prints_without_decimals_but_a_fractional_one_keeps_them()
    {
        // His example reads "EUR 13500", not "EUR 13500.00" — but silently rounding a real
        // fractional price would print a number that does not match the line total.
        Assert.Contains("Original EUR 13500 -> USD 15329",
            WebshopInvoiceLineComposer.ComposeConversionNote("EUR", "USD", 13500m, 15329m),
            StringComparison.Ordinal);

        Assert.Contains("Original EUR 99.5 -> USD 112.75",
            WebshopInvoiceLineComposer.ComposeConversionNote("EUR", "USD", 99.5m, 112.75m),
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    //  (c) + (d) line 1 keeps the order number and gains the date in ()
    // ---------------------------------------------------------------------

    [Fact]
    public void Line_one_keeps_the_web_order_number_and_adds_the_order_date_in_parentheses()
    {
        var header = WebshopInvoiceLineComposer.ComposeHeaderDescription("12345", OrderDate);

        Assert.StartsWith("Sponsor Webshop Order: 12345 (2026-08-04)", header, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_one_carries_no_time_of_day()
    {
        // The retired script interpolated a full local timestamp, which put a meaningless
        // "14:23:11" on a customer-facing invoice.
        var header = WebshopInvoiceLineComposer.ComposeHeaderDescription("12345", OrderDate);

        Assert.DoesNotContain(":23", header, StringComparison.Ordinal);
        Assert.DoesNotContain("14:", header, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_order_date_prints_no_empty_brackets()
    {
        var header = WebshopInvoiceLineComposer.ComposeHeaderDescription("12345", null);

        Assert.DoesNotContain("(", header, StringComparison.Ordinal);
        Assert.StartsWith("Sponsor Webshop Order: 12345", header, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    //  (e) every OTHER row drops the number and the date
    // ---------------------------------------------------------------------

    [Fact]
    public void A_product_row_no_longer_repeats_the_order_number_or_the_order_date()
    {
        var description = WebshopInvoiceLineComposer.ComposeProductDescription("Booth package", null);

        Assert.Equal("Booth package", description);
        Assert.DoesNotContain("Webshop Order:", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Order Date:", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_product_row_with_a_conversion_is_the_name_then_a_blank_line_then_the_note()
    {
        var note = WebshopInvoiceLineComposer.ComposeConversionNote("EUR", "USD", 13500m, 15329m);
        var description = WebshopInvoiceLineComposer.ComposeProductDescription("Booth package", note);

        Assert.Equal("Booth package\r\n\r\n" + note, description);
    }

    // ---------------------------------------------------------------------
    //  The whole line set
    // ---------------------------------------------------------------------

    [Fact]
    public void A_full_order_composes_header_first_then_one_line_per_product()
    {
        var lines = WebshopInvoiceLineComposer.Compose(
            orderNumber: "12345",
            orderDate: OrderDate,
            orderLines: new[]
            {
                new WebshopOrderLine("Booth package", 1m, 13500m),
                new WebshopOrderLine("Extra staff ticket", 2m, 250m),
            },
            vatZoneNumber: 1,
            convert: eur => (eur, null));

        Assert.Equal(3, lines.Count);

        // Header: text only — no product, no price. It is a label, not something billed.
        Assert.Equal(1, lines[0].LineNumber);
        Assert.Null(lines[0].Quantity);
        Assert.Null(lines[0].UnitNetPrice);
        Assert.Null(lines[0].ProductNumber);
        Assert.Contains("12345 (2026-08-04)", lines[0].Description, StringComparison.Ordinal);

        Assert.Equal("Booth package", lines[1].Description);
        Assert.Equal(1m, lines[1].Quantity);
        Assert.Equal(13500m, lines[1].UnitNetPrice);
        Assert.Equal("1000", lines[1].ProductNumber);

        Assert.Equal("Extra staff ticket", lines[2].Description);
        Assert.Equal(2m, lines[2].Quantity);
        Assert.Equal(3, lines[2].LineNumber);

        // The order number appears exactly ONCE across the whole invoice — that is (c)+(e) together.
        var occurrences = lines.Count(l => l.Description.Contains("12345", StringComparison.Ordinal));
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void A_converted_order_puts_the_note_on_every_product_row_and_bills_the_converted_price()
    {
        var lines = WebshopInvoiceLineComposer.Compose(
            orderNumber: "12345",
            orderDate: OrderDate,
            orderLines: new[] { new WebshopOrderLine("Booth package", 1m, 13500m) },
            vatZoneNumber: 2,
            convert: eur => (
                eur * 1.1354m,
                WebshopInvoiceLineComposer.ComposeConversionNote("EUR", "USD", eur, eur * 1.1354m)));

        Assert.Equal(13500m * 1.1354m, lines[1].UnitNetPrice);
        Assert.Contains("Currency conversion applied:\r\nEUR -> USD", lines[1].Description,
            StringComparison.Ordinal);

        // 🔒 The header line must NOT carry a conversion note — it has no price to convert, and a
        // currency note beside no amount reads as though the whole invoice was restated.
        Assert.DoesNotContain("Currency conversion", lines[0].Description, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    //  VAT zone -> product number
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(1, "1000")]
    [InlineData(2, "2000")]
    [InlineData(3, "2000")]
    [InlineData(4, "2000")]
    public void The_vat_zone_selects_the_product_that_carries_the_tax_treatment(int zone, string expected)
        => Assert.Equal(expected, WebshopInvoiceLineComposer.ResolveProductNumber(zone));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void An_unknown_vat_zone_throws_rather_than_guessing_a_tax_treatment(int zone)
    {
        // Defaulting here would invoice the wrong VAT and nothing downstream would notice.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WebshopInvoiceLineComposer.ResolveProductNumber(zone));
    }
}
