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
            + Row("Ticket class", Enc(ticketClassLabel))
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
            + "</strong> ticket(s) of this class, the partner cannot claim what they have paid for. "
            + "The hub tracks the balance either way, but it cannot open or close the code.</p>";

        await _alerts.AlertAsync(
            subject, body, ct, throttleKey: null,
            recipient: ZohoChangeNotifier.ActionableRecipient);

        _log?.LogInformation(
            "§990: asked for the Backstage promo code '{Coupon}' ({Qty} × {Class}, total {Total}).",
            couponName, quantity, ticketClassLabel, totalPurchased);
        return true;

        static string Row(string label, string value) =>
            "<tr>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;color:#6b7280;\">{label}</td>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\"><strong>{value}</strong></td>"
            + "</tr>";
    }
}
