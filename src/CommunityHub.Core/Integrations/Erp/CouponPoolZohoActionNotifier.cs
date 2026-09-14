using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §990 — mails the actionable mailbox the ONE thing CEH cannot do for a new prepaid purchase:
/// create (or raise) the promo code in Zoho Backstage.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: *"i must also get an email which states to create the coupon inside
/// zoho with the amount + coupon name + ticket class, as there is no api in zoho to do this"*.</para>
///
/// <para>🔑 <b>This is not a missing integration, it is a missing API</b> — §787.14: Zoho Backstage
/// exposes no coupon endpoint at all. The hub already relies on that fact in two places (§795.1: a
/// closed pool does not close the promo code; §796: an exhausted pool leaves the code working and the
/// next claimant takes a ticket nobody paid for). Both of those tell him about a code that is now
/// WRONG. Nothing told him about a code that does not exist YET — so a partner could be invoiced for
/// 20 tickets they had no way to claim.</para>
///
/// <para>🔒 <b>Sent for a top-up too, with different wording.</b> Buying 25 more against an existing
/// agreement raises what the code must allow; leaving the old limit in place is the §796 shape
/// arriving from the other direction — the partner paid and then hits a code that is used up.</para>
///
/// <para>🔒 <b>No throttle key.</b> Each mail names a specific purchase, so suppressing the second
/// one because a first went out an hour ago would silently lose an instruction. Same reasoning as
/// <see cref="DraftInvoiceCreatedNotifier"/>.</para>
/// </remarks>
public sealed class CouponPoolZohoActionNotifier
{
    private readonly EngineAlertSender _alerts;
    private readonly ILogger<CouponPoolZohoActionNotifier>? _log;

    public CouponPoolZohoActionNotifier(
        EngineAlertSender alerts, ILogger<CouponPoolZohoActionNotifier>? log = null)
    {
        _alerts = alerts;
        _log = log;
    }

    /// <summary>
    /// Tell him to set the promo code up in Backstage for a purchase just recorded.
    /// </summary>
    /// <param name="couponName">The promo code as it must exist in Backstage.</param>
    /// <param name="ticketClassLabel">Which ticket class the code must be restricted to.</param>
    /// <param name="quantity">How many tickets this purchase added.</param>
    /// <param name="totalPurchased">The pool's new total across all purchases.</param>
    /// <param name="isTopUp">False when this purchase created the pool.</param>
    /// <param name="invoiceNote">
    /// One line about the invoice, or null. Included so the mail answers "was this billed?" without
    /// a second lookup — the two facts are read together or not at all.
    /// </param>
    public async Task<bool> NotifyAsync(
        string couponName,
        string ticketClassLabel,
        int quantity,
        int totalPurchased,
        bool isTopUp,
        string? invoiceNote,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(couponName)) return false;

        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

        var verb = isTopUp
            ? "RAISE the promo code's limit in Zoho Backstage"
            : "CREATE the promo code in Zoho Backstage";

        var subject = isTopUp
            ? $"Zoho Backstage: raise promo code '{couponName}' to {totalPurchased} ticket(s)"
            : $"Zoho Backstage: create promo code '{couponName}' for {quantity} ticket(s)";

        var body =
            $"<p><strong>ACTION NEEDED — {Enc(verb)}.</strong> "
            + "CEH has recorded the purchase and cannot do this part: Backstage has "
            + "<strong>no coupon API</strong>, so the promo code is created and limited by hand.</p>"
            + "<table style=\"border-collapse:collapse;font-size:14px;\">"
            + Row("Coupon / promo code", Enc(couponName))
            // §1116 — the NAME, not the id. ⚠️ Unlike the customer-facing mails this one keeps the id
            // when that is all we have, because it is an OPS instruction and the id is the handle he
            // matches in Backstage — but it is labelled as an id rather than offered as the name.
            + Row("Ticket class",
                  HumanLabel.IsMachineId(ticketClassLabel)
                      ? $"<em>not named in CEH yet</em> (Backstage id {Enc(ticketClassLabel)})"
                      : Enc(ticketClassLabel))
            + Row(isTopUp ? "Tickets added now" : "Tickets bought",
                  quantity.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + Row("Total the code must allow",
                  totalPurchased.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + "</table>"
            + (string.IsNullOrWhiteSpace(invoiceNote)
                ? string.Empty
                : $"<p style=\"font-size:14px;\">{Enc(invoiceNote)}</p>")
            + "<p style=\"color:#6b7280;font-size:13px;\">Until the code exists and allows "
            + "<strong>" + totalPurchased.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "</strong> ticket(s) of this class, the customer cannot claim what they have paid for. "
            + "The hub tracks the balance either way, but it cannot open or close the code.</p>";

        await _alerts.AlertAsync(
            subject, body, ct, throttleKey: null,
            recipient: ZohoChangeNotifier.ActionableRecipient);

        _log?.LogInformation(
            "§990: asked for the Backstage promo code '{Coupon}' ({Qty} × {Class}, total {Total}).",
            couponName, quantity, ticketClassLabel, totalPurchased);
        return true;

        static string Row(string label, string value) => TableRow(label, value);
    }

    /// <summary>
    /// §1091 — the same instruction for an <b>AD-HOC</b> coupon: create the promo code, and set the
    /// attendee's discount so the split the hub will invoice actually happens.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19, on being told an ad-hoc coupon fires no Backstage instruction at
    /// all: *"rgr 2, create that"*. Until now only the PREPAID path mailed one, so a 50/50 coupon
    /// could be configured in CEH and the partner still charged full price at checkout, because
    /// nothing reminded anyone to put the discount on the code.</para>
    ///
    /// <para>🔴 <b>THE ATTENDEE'S DISCOUNT IS ONLY DERIVABLE WHEN THERE IS NO AGREED PRICE, and this
    /// mail refuses to guess it otherwise.</b> With no agreed price both shares are percentages of
    /// the same ticket price, so the attendee's discount is exactly <c>100 − share</c>. With an
    /// agreed price of DKK <i>A</i> the company pays <c>A × share</c>, which has no fixed
    /// relationship to the ticket's list price — so "100 − share" would be a plausible number that
    /// is simply wrong, and printing it on an instruction he will follow is worse than printing
    /// nothing. In that case the mail states the DKK amount the company will be billed and leaves
    /// the discount to him.</para>
    ///
    /// <para>🔒 Sent only when the coupon is created or its split CHANGES — not on every Save. A
    /// mail per keystroke on the notes field is the §302 "70-mail night" rebuilt.</para>
    /// </remarks>
    public async Task<bool> NotifyAdHocAsync(
        string couponName,
        decimal? agreedUnitPriceDkk,
        int? invoicedSharePercent,
        int? erpCustomerNumber,
        bool isNew,
        CancellationToken ct = default,
        // §1094 — the agreed ticket ceiling, or null when uncapped. Backstage is where the limit is
        // actually enforced, so it belongs in the instruction rather than only in the hub.
        int? capTickets = null,
        // 🔴 §1109 — the customer's NAME (operator 2026-08-21: *"right now some of the emails show
        // only customer id, which makes no value to anyone"*). A bare 28101082 is a number he then
        // has to look up in e-conomic to know who the mail is about.
        string? customerName = null)
    {
        if (string.IsNullOrWhiteSpace(couponName)) return false;

        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        static string Inv(decimal d) => d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        var share = CouponBillableShare.Percent(invoicedSharePercent);
        var hasAgreed = agreedUnitPriceDkk is { } a && a > 0m;

        var verb = isNew
            ? "CREATE the promo code in Zoho Backstage"
            : "CHECK the promo code's discount in Zoho Backstage";

        var subject = isNew
            ? $"Zoho Backstage: create promo code '{couponName}'"
            : $"Zoho Backstage: promo code '{couponName}' — the split changed";

        var rows =
            TableRow("Coupon / promo code", Enc(couponName))
            + TableRow("Linked company is invoiced",
                $"{share}% {(hasAgreed ? "of the agreed price" : "of the ticket price")}")
            + (hasAgreed
                ? TableRow("Agreed price per ticket", $"{Inv(agreedUnitPriceDkk!.Value)} DKK")
                : string.Empty)
            // 🔴 §1109 — NAME FIRST, number in brackets. A bare "28101082" is a lookup, not an
            // answer: he has to open e-conomic to learn who the mail is even about. The number stays
            // because it is what he searches on, but it is the qualifier, not the identity.
            + (erpCustomerNumber is > 0
                ? TableRow("Invoiced to",
                    (string.IsNullOrWhiteSpace(customerName)
                        ? "<em>customer</em> "
                        : $"<strong>{Enc(customerName)}</strong> ")
                    + $"(e-conomic {erpCustomerNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)})")
                : TableRow("Invoiced to", "<em>no customer set — this coupon cannot be invoiced yet</em>"))
            // §1094 — the ceiling Backstage must enforce. Stated even when uncapped, because
            // "no limit" is itself a setting on the promo code and leaving it ambiguous is how a
            // code ends up capped at Zoho's default while the hub reports it as open.
            + TableRow("Ticket limit to set on the code",
                capTickets is > 0
                    ? $"<strong>{capTickets.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}</strong>"
                    : "no limit");

        // The one instruction that matters, and it is honest about what it cannot work out.
        string discountLine;
        if (hasAgreed)
        {
            // Both arguments are the agreed price on purpose: there is no ticket in hand here, and
            // the fallback must never be reachable. Passing 0 as "the actual price" would give the
            // same answer today and a silent 0 the day `Basis` changes.
            var billed = CouponBillableShare.Dkk(
                agreedUnitPriceDkk!.Value, agreedUnitPriceDkk, invoicedSharePercent);
            discountLine =
                "<p style=\"font-size:14px;\"><strong>Set the attendee's discount yourself.</strong> "
                + $"This coupon has an agreed price ({Inv(agreedUnitPriceDkk!.Value)} DKK), so the "
                + $"company is billed <strong>{Inv(billed)} DKK</strong> per ticket regardless of the "
                + "ticket's list price. CEH cannot work out the matching discount percentage from "
                + "that, and will not guess at a number you would be typing into Backstage.</p>";
        }
        else if (share >= CouponBillableShare.DefaultPercent)
        {
            discountLine =
                "<p style=\"font-size:14px;\">The linked company is billed the <strong>whole "
                + "ticket</strong>, so the promo code should give a <strong>100% discount</strong> — "
                + "the attendee pays nothing at checkout.</p>";
        }
        else
        {
            discountLine =
                "<p style=\"font-size:14px;\">Set the promo code to give the attendee a "
                + $"<strong>{100 - share}% discount</strong>. They pay that {100 - share}% by card at "
                + $"checkout, and the linked company is invoiced the other <strong>{share}%</strong>.</p>";
        }

        var body =
            $"<p><strong>ACTION NEEDED — {Enc(verb)}.</strong> "
            + "CEH records the split and raises the invoice, but cannot do this part: Backstage has "
            + "<strong>no coupon API</strong>, so the code and its discount are set by hand.</p>"
            + "<table style=\"border-collapse:collapse;font-size:14px;\">" + rows + "</table>"
            + discountLine
            + "<p style=\"color:#6b7280;font-size:13px;\">If the discount in Backstage does not match "
            + "the split above, the two halves will not add up — the attendee pays the wrong amount "
            + "and the company is still invoiced its share. <strong>Nothing in CEH can detect "
            + "that</strong>, because the agreed price and the list price are allowed to differ.</p>";

        await _alerts.AlertAsync(
            subject, body, ct, throttleKey: null,
            recipient: ZohoChangeNotifier.ActionableRecipient);

        _log?.LogInformation(
            "§1091: asked for the Backstage promo code '{Coupon}' (company share {Share}%, agreed "
            + "price {Agreed}, new={IsNew}).",
            couponName, share, agreedUnitPriceDkk, isNew);
        return true;
    }

    private static string TableRow(string label, string value) =>
        "<tr>"
        + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;color:#6b7280;\">{label}</td>"
        + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\"><strong>{value}</strong></td>"
        + "</tr>";
}
