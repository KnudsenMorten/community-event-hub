using System.Globalization;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §787 — composes the description lines on a coupon draft invoice.
/// </summary>
/// <remarks>
/// <para>Ported from the retired <c>Create-ERP-Invoice-Coupon-Tickets.ps1</c>. Deliberately a
/// SEPARATE composer from <see cref="WebshopInvoiceLineComposer"/>, because the two invoices are
/// genuinely different documents and merging them would force one to carry the other's shape:</para>
///
/// <list type="bullet">
///   <item>the webshop invoice has a HEADER line carrying the order number; a coupon invoice has
///         no header — every line is a billable ticket;</item>
///   <item>the webshop converts from <b>EUR</b>, coupons from <b>DKK</b> (Zoho reports ticket
///         prices in DKK);</item>
///   <item>the webshop rounds with <c>Ceiling</c>, coupons to <b>2 decimals</b>. 🔒 Both are ported
///         as-is: they are the operator's existing commercial behaviour, and quietly unifying them
///         would change the amount on real invoices he already issues.</item>
/// </list>
///
/// <para>⚠️ <b>What IS shared, and it is a judgment call worth stating.</b> The currency note reuses
/// <see cref="WebshopInvoiceLineComposer.ComposeConversionNote"/> — so §786.1(f)'s three-line format
/// applies to coupon invoices too. He specified that wording for the WEBSHOP script only. It is
/// applied here because the alternative is two different renderings of the same sentence on invoices
/// from the same company, and the new wording is strictly clearer. **Flagged in §787 for him to
/// reject if he wants the coupon invoices left alone.**</para>
/// </remarks>
public static class CouponInvoiceLineComposer
{
    /// <summary>Zoho reports coupon ticket prices in DKK.</summary>
    public const string SourceCurrency = CouponClaimExtractor.SourceCurrency;

    /// <summary>
    /// One ticket's description block. Single newlines between the facts (not blank lines): these
    /// are fields about one ticket, not separate sections, and the retired script rendered them the
    /// same way.
    /// </summary>
    public static string ComposeTicketDescription(
        CouponClaim claim, string? conversionNote, string? shareNote = null)
    {
        var attendee = $"{claim.FirstName} {claim.LastName}".Trim();

        var parts = new List<string>();

        // §1116 — see ComposePrepaidDescription: a class id is not a ticket class, and this line is
        // read by the customer being billed. Dropped rather than printed as a number; the attendee
        // and the coupon below already say what was bought and under which agreement.
        if (!IsMachineId(claim.TicketClassName)) parts.Add($"Ticket Class: {claim.TicketClassName}");

        parts.Add($"Attendee: {attendee}");
        parts.Add($"Email: {claim.Email}");
        parts.Add($"Coupon: {claim.CouponName}");
        // ⚠️ The Zoho ids STAY: they are the reconciliation handle a finance person matches against
        // Backstage when a line is queried, and they are labelled as system ids rather than offered
        // as the name of anything.
        parts.Add($"Zoho OrderId: {claim.OrderId}");
        parts.Add($"Zoho TicketId: {claim.TicketId}");

        // §1091 — say WHY this is not the ticket price, before the currency note. A partner reading
        // "1,500.00" against a 3,000 ticket writes an e-mail; "50% of 3,000.00 (agreed price)" does
        // not. Absent at a plain 100% of the ticket price, where there is nothing to explain.
        if (!string.IsNullOrEmpty(shareNote))
        {
            parts.Add(shareNote);
        }

        if (!string.IsNullOrEmpty(conversionNote))
        {
            parts.Add(conversionNote);
        }

        return string.Join(WebshopInvoiceLineComposer.NewLine, parts);
    }

    /// <summary>
    /// All lines for one coupon invoice — one per claimed ticket, quantity 1 each.
    /// <paramref name="convert"/> maps a DKK unit price into the invoice currency and returns the
    /// note to print (null when the customer already invoices in DKK).
    /// </summary>
    /// <param name="agreedUnitPriceDkk">
    /// §1091 — the coupon's agreed price per ticket, or null to bill what the ticket actually cost.
    /// Operator 2026-08-19: *"prio 1: custom (agreed) price and prio 2 if prio 1 is not filled out:
    /// use zoho price"*.
    /// </param>
    /// <param name="invoicedSharePercent">
    /// §1091 — the percentage of that price the LINKED COMPANY is invoiced, or null for 100.
    /// </param>
    public static IReadOnlyList<ComposedInvoiceLine> Compose(
        IEnumerable<CouponClaim> claims,
        int vatZoneNumber,
        Func<decimal, (decimal Converted, string? Note)> convert,
        decimal? agreedUnitPriceDkk = null,
        int? invoicedSharePercent = null)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(convert);

        // Same VAT-zone → product mapping as the webshop invoice; it is a property of the AGREEMENT,
        // not of the document, so it is shared rather than duplicated.
        var productNumber = WebshopInvoiceLineComposer.ResolveProductNumber(vatZoneNumber);

        var lines = new List<ComposedInvoiceLine>();
        var lineNumber = 0;

        foreach (var claim in claims)
        {
            lineNumber++;

            // 🔑 §1091 — the share is taken in DKK, BEFORE conversion. The agreed price is agreed in
            // DKK, so a percentage of an already-converted figure would fold the FX rounding into
            // the share. Convert what the partner owes, never the other way round.
            var billableDkk = CouponBillableShare.Dkk(
                claim.UnitPriceDkk, agreedUnitPriceDkk, invoicedSharePercent);
            var shareNote = CouponBillableShare.ComposeShareNote(
                claim.UnitPriceDkk, agreedUnitPriceDkk, invoicedSharePercent);

            var (converted, note) = convert(billableDkk);

            lines.Add(new ComposedInvoiceLine(
                lineNumber,
                ComposeTicketDescription(claim, note, shareNote),
                Quantity: 1m,
                // 2 decimals, as the retired script did — NOT the webshop's Ceiling.
                UnitNetPrice: Math.Round(converted, 2, MidpointRounding.AwayFromZero),
                ProductNumber: productNumber));
        }

        return lines;
    }

    /// <summary>The currency note for a coupon line — DKK to the customer's currency.</summary>
    public static string ComposeConversionNote(string toCurrency, decimal originalDkk, decimal converted) =>
        WebshopInvoiceLineComposer.ComposeConversionNote(
            SourceCurrency, toCurrency, originalDkk, converted);

    /// <summary>
    /// §814 — the SUB-HEADING (<c>notes.textLine1</c>) of a coupon invoice: which agreement it bills.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>This used to be the invoice's whole HEADING, and it did not match the house
    /// format.</b> A coupon invoice would have reached a partner headed *"Coupon tickets: ARROW-DK"*
    /// while every sponsorship invoice from the same company is headed *"ELDK27 - Experts Live
    /// Denmark"* (§811(b) — read off the retired script, and confirmed by the first invoice he
    /// actually sent). One company, two letterheads.</para>
    ///
    /// <para>🔑 <b>Caught before a partner saw it, which is the whole point.</b> No coupon invoice
    /// has ever been created: §787.5 records that the coupon script never ran, so unlike §786 there
    /// is no gold standard to diff against. The webshop invoice needed FOUR corrections after it
    /// reached a real customer — this one is being aligned while being wrong still costs nothing.</para>
    ///
    /// <para>⇒ The heading is now the company line and the coupon name moves here, which is exactly
    /// the webshop invoice's shape: heading = who is invoicing, <c>textLine1</c> = what for. An
    /// organizer still sees which agreement a draft came from, one line lower.</para>
    /// </remarks>
    public static string ComposeSubHeading(string couponName) =>
        string.Create(CultureInfo.InvariantCulture, $"Coupon tickets: {couponName}");

    /// <summary>§990 — the label the coupon's free-text notes print under on the invoice.</summary>
    /// <remarks>
    /// Neutral on purpose. The webshop invoice labels its block <c>PurchaseOrder Info:</c> because
    /// that field IS a PO; this one is whatever the partner needs on their invoice — operator
    /// 2026-08-09: *"it can be for eample purchase order number or other relevant info needed"* — so
    /// a PO-specific label would be wrong for most of what goes in it.
    /// </remarks>
    public const string NotesLabel = "Notes:";

    /// <summary>
    /// §990 — the sub-heading (<c>notes.textLine1</c>) WITH the coupon's own notes appended as their
    /// own block. Used by BOTH coupon invoice types (prepaid and ad-hoc), which is the whole point:
    /// the note is a property of the AGREEMENT, so it belongs on every invoice that agreement
    /// produces.
    /// </summary>
    /// <remarks>
    /// <para>Shape is the webshop PO block exactly (§786.1(e)) — a blank row, the label, then the
    /// text on its own line — because these invoices leave the same company and a second layout for
    /// the same idea is how two documents start looking unrelated.</para>
    ///
    /// <para>⚠️ <b>Blank notes print NOTHING</b>, not a bare <c>Notes:</c> label. The §786.1
    /// reasoning applies unchanged: an empty labelled block reads to a customer as text that failed
    /// to load rather than text that was never entered, and that is a support call about a fault
    /// that did not happen.</para>
    ///
    /// <para>🔒 <b>Deliberately the free-text header, not <c>references.other</c>.</b> That field
    /// carries the idempotency marker every "already invoiced" check scans for (§787.3/§817.1);
    /// putting operator prose there would either overwrite the interlock or force it onto a
    /// substring match.</para>
    /// </remarks>
    public static string ComposeSubHeading(string couponName, string? notes)
    {
        var head = ComposeSubHeading(couponName);
        var text = (notes ?? string.Empty).Trim();

        return text.Length == 0
            ? head
            : head + WebshopInvoiceLineComposer.BlockSeparator
                   + NotesLabel + WebshopInvoiceLineComposer.NewLine + text;
    }

    /// <summary>
    /// §990 — the description block for a PREPAID ticket purchase: one line covering N tickets of a
    /// class, rather than the one-line-per-attendee shape a claim invoice has.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>Why it cannot reuse <see cref="ComposeTicketDescription"/>.</b> That block names an
    /// attendee, an e-mail and a Zoho ticket id — a prepaid purchase has **none of them**, because
    /// nobody has claimed a ticket yet; that is what "prepaid" means. Rendering it with those fields
    /// blank would put empty labels on a partner's invoice.
    /// </remarks>
    public static string ComposePrepaidDescription(
        string ticketClassLabel, string couponName, int quantity, string? conversionNote)
    {
        var parts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Prepaid tickets: {quantity}"),
        };

        // 🔴 §1116 — NEVER PRINT A MACHINE ID ON A CUSTOMER'S INVOICE. Operator 2026-08-21:
        // *"stop using numbers like this - people dont understand a ticket class number - use the
        // displayname"* — after a real Arrow invoice printed
        // `Ticket Class: 14880000003485482`.
        //
        // 🔑 Enforced HERE, at the last place before the customer reads it, rather than by fixing
        // the caller. Every caller resolves this label with a fallback chain that ENDS IN THE ID
        // (§1013b), so a class nobody has claimed yet, or a pool created before its class had a
        // name, produces the id — and then a page shows it harmlessly while an invoice shows it to
        // a paying customer. Two callers today; a guard in one of them is a guard in neither.
        //
        // 🔒 A line is DROPPED, never faked. The coupon name below already identifies the agreement,
        // so an invoice without this line is complete and readable; an invoice with an 17-digit
        // number in it is neither.
        if (!IsMachineId(ticketClassLabel)) parts.Add($"Ticket Class: {ticketClassLabel}");

        parts.Add($"Coupon: {couponName}");

        if (!string.IsNullOrEmpty(conversionNote)) parts.Add(conversionNote);

        return string.Join(WebshopInvoiceLineComposer.NewLine, parts);
    }

    /// <summary>§1116 — see <see cref="HumanLabel.IsMachineId"/>, which every renderer shares.</summary>
    private static bool IsMachineId(string? value) => HumanLabel.IsMachineId(value);
}
