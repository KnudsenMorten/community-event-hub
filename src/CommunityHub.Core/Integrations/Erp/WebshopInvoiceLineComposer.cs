using System.Globalization;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>One webshop order line, as the invoice composer needs it. Prices are in EUR — the
/// webshop's own currency — and are converted per invoice currency by the caller.</summary>
public sealed record WebshopOrderLine(
    string ProductName,
    decimal Quantity,
    decimal UnitPriceEur);

/// <summary>One composed e-conomic draft-invoice line, ready to serialize.</summary>
/// <param name="LineNumber">1-based; also used as the sort key.</param>
/// <param name="Description">The multi-line text e-conomic prints on the invoice.</param>
/// <param name="Quantity">Null on the header line, which carries text only.</param>
/// <param name="UnitNetPrice">Null on the header line. In the INVOICE currency, already converted.</param>
/// <param name="ProductNumber">Null on the header line.</param>
public sealed record ComposedInvoiceLine(
    int LineNumber,
    string Description,
    decimal? Quantity = null,
    decimal? UnitNetPrice = null,
    string? ProductNumber = null);

/// <summary>
/// §786.1 — composes the DESCRIPTIONS on a webshop draft invoice, exactly as the operator specified.
/// </summary>
/// <remarks>
/// <para>Pure and I/O-free on purpose. The wording on an invoice line is the part of this migration
/// the operator described down to the line break, so it is the part that gets asserted character for
/// character rather than eyeballed on a draft.</para>
///
/// <para><b>What he asked for, and what it replaces</b> (operator 2026-08-04). The retired
/// PowerShell script repeated the order number and the order date on EVERY product row:</para>
/// <code>
/// Sponsor Webshop Order: 12345          ← line 1
///
/// Booth package                          ← line 2
///
/// Webshop Order: 12345                   ← redundant
///
/// Order Date: 04-08-2026 14:23:11        ← redundant
///
/// Currency conversion applied: EUR -> USD
/// Original EUR 13500 -> USD 15329
/// </code>
/// <para>His four changes: keep the order number on line 1, ADD the order date to line 1 in
/// parentheses, DROP the number and date from every other product row (<i>"this is redundant
/// information"</i>), and break the currency note so the label stands on its own line.</para>
///
/// <para>⚠️ <b>The date format on line 1 is a JUDGMENT CALL, and it is flagged rather than
/// buried.</b> He said only <i>"add web order date on line 1 in ()"</i>. This uses ISO
/// <c>yyyy-MM-dd</c> because it cannot be misread as either a Danish or a US date; the retired
/// script interpolated a full local timestamp, which put a meaningless <c>14:23:11</c> on a customer
/// invoice. If he wants Danish <c>dd-MM-yyyy</c>, it is this one constant.</para>
///
/// <para>🔒 Line breaks are <c>\r\n</c>, matching the retired script exactly. That is the form
/// e-conomic has been storing and printing for these invoices, so it is the form that is KNOWN to
/// survive the round trip — an invisible character choice is not the place to improve on a working
/// system.</para>
/// </remarks>
public static class WebshopInvoiceLineComposer
{
    /// <summary>The line break e-conomic descriptions use (as the retired script wrote them).</summary>
    public const string NewLine = "\r\n";

    /// <summary>Blank line between the BLOCKS of a description (name, then the currency note).</summary>
    public const string BlockSeparator = NewLine + NewLine;

    /// <summary>§786.1(d) — the order-date format on line 1. See the remarks: deliberate, and the
    /// single place to change it if he wants Danish notation.</summary>
    public const string OrderDateFormat = "yyyy-MM-dd";

    /// <summary>
    /// §786.1(c)+(d) — line 1: the web order number, and now the order date in parentheses.
    /// A missing date degrades to the number alone rather than printing empty brackets.
    /// </summary>
    public static string ComposeHeaderDescription(string orderNumber, DateTimeOffset? orderDate)
    {
        var head = $"Sponsor Webshop Order: {orderNumber}";

        if (orderDate is { } d)
        {
            head += $" ({d.ToString(OrderDateFormat, CultureInfo.InvariantCulture)})";
        }

        // The trailing blank line is what separates the header from the first product row on the
        // printed invoice; the retired script emitted it too.
        return head + BlockSeparator;
    }

    /// <summary>§821 — the label above the customer's purchase-order reference, as he typed it.</summary>
    /// <remarks>
    /// 🔒 <c>PurchaseOrder Info:</c>, from the screenshot of e-conomic's *Noter og referencer* dialog
    /// he filled in by hand. He had typed <c>PurchaseInfo:</c> in the message a minute earlier; the
    /// screenshot is the correction. Do not "tidy" this into `PO reference` or similar — a customer's
    /// finance department scans for the exact phrase they were told to look for.
    /// </remarks>
    public const string PurchaseOrderLabel = "PurchaseOrder Info:";

    /// <summary>
    /// §821 — the invoice's <c>notes.textLine1</c>: the standing sub-heading, then the customer's
    /// PURCHASE-ORDER reference from Company Manager (<c>billing_reference</c>).
    /// </summary>
    /// <remarks>
    /// <para>Produces exactly the block the operator typed into e-conomic by hand:</para>
    /// <code>
    /// Sponsorship
    ///                       ← a blank row ("1 extra row")
    /// PurchaseOrder Info:
    /// POCUG000347
    /// </code>
    ///
    /// <para>🔑 <b>Why the header rather than a reference field:</b> <c>references.other</c> already
    /// carries <c>WebshopOrderId-&lt;n&gt;</c>, the interlock that stops one order being invoiced
    /// twice (§817.1). Putting the PO there would either overwrite the interlock or force the
    /// already-invoiced check onto a substring match. The header is free text and carries no machine
    /// meaning, which is exactly what makes it safe.</para>
    ///
    /// <para>⚠️ <b>A blank reference prints NOTHING</b> — not a bare label. On a customer's invoice an
    /// empty <c>PurchaseOrder Info:</c> reads as a PO that failed to load rather than one that was
    /// never given, and that is a support call about a system fault that did not happen.</para>
    /// </remarks>
    public static string ComposeHeaderTextLine(string subHeading, string? purchaseOrderReference)
    {
        var head = (subHeading ?? string.Empty).Trim();
        var po = (purchaseOrderReference ?? string.Empty).Trim();

        if (po.Length == 0) return head;

        return head + BlockSeparator + PurchaseOrderLabel + NewLine + po;
    }

    /// <summary>
    /// §786.1(f) — the currency note, broken over three lines exactly as he wrote it:
    /// <code>
    /// Currency conversion applied:
    /// EUR -> USD
    /// Original EUR 13500 -> USD 15329
    /// </code>
    /// The label used to share line 1 with the currency pair; he asked for the newline after the
    /// colon.
    /// </summary>
    public static string ComposeConversionNote(
        string fromCurrency, string toCurrency, decimal originalAmount, decimal convertedAmount)
    {
        var from = Normalize(fromCurrency);
        var to = Normalize(toCurrency);

        return string.Join(NewLine,
            "Currency conversion applied:",
            $"{from} -> {to}",
            $"Original {from} {Amount(originalAmount)} -> {to} {Amount(convertedAmount)}");
    }

    /// <summary>
    /// §786.1(e) — a product row is now the product NAME plus, when one applies, the currency note.
    /// The web order number and order date are deliberately absent: they are on line 1, and
    /// repeating them per row is the redundancy he asked to remove.
    /// </summary>
    public static string ComposeProductDescription(string productName, string? conversionNote)
    {
        var name = (productName ?? string.Empty).Trim();

        return string.IsNullOrEmpty(conversionNote)
            ? name
            : name + BlockSeparator + conversionNote;
    }

    /// <summary>
    /// The e-conomic product number that carries the VAT treatment. Zone 1 is domestic (taxed);
    /// 2, 3 and 4 are all untaxed for different reasons but bill against the same product.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An unknown zone — never guessed, because
    /// guessing here would invoice the wrong VAT.</exception>
    public static string ResolveProductNumber(int vatZoneNumber) => vatZoneNumber switch
    {
        1 => "1000",              // Denmark — with tax
        2 or 3 or 4 => "2000",    // EU / outside EU / domestic-no-VAT — without tax
        _ => throw new ArgumentOutOfRangeException(
            nameof(vatZoneNumber), vatZoneNumber,
            "Unsupported e-conomic VAT zone. Expected 1-4; refusing to guess a VAT treatment."),
    };

    /// <summary>
    /// Builds the whole line set for one order: the header line, then one line per product.
    /// <paramref name="convert"/> maps a EUR unit price to the invoice currency and is given the
    /// chance to describe itself — it returns both the converted amount and the note to print (null
    /// when the invoice is already in EUR and nothing was converted).
    /// </summary>
    public static IReadOnlyList<ComposedInvoiceLine> Compose(
        string orderNumber,
        DateTimeOffset? orderDate,
        IEnumerable<WebshopOrderLine> orderLines,
        int vatZoneNumber,
        Func<decimal, (decimal Converted, string? Note)> convert)
    {
        ArgumentNullException.ThrowIfNull(orderLines);
        ArgumentNullException.ThrowIfNull(convert);

        var productNumber = ResolveProductNumber(vatZoneNumber);
        var composed = new List<ComposedInvoiceLine>();
        var lineNumber = 1;

        composed.Add(new ComposedInvoiceLine(
            lineNumber, ComposeHeaderDescription(orderNumber, orderDate)));

        foreach (var line in orderLines)
        {
            lineNumber++;
            var (converted, note) = convert(line.UnitPriceEur);

            composed.Add(new ComposedInvoiceLine(
                lineNumber,
                ComposeProductDescription(line.ProductName, note),
                line.Quantity,
                converted,
                productNumber));
        }

        return composed;
    }

    private static string Normalize(string currency) =>
        (currency ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>
    /// Amounts print without trailing zeros — his example reads <c>EUR 13500</c>, not
    /// <c>EUR 13500.00</c> — but a genuine fractional amount keeps its decimals rather than being
    /// rounded away into a number that does not match the line total.
    /// </summary>
    private static string Amount(decimal value) =>
        value == decimal.Truncate(value)
            ? decimal.Truncate(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
}
