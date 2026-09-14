using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §1094 — a partner has asked for more tickets from their own status page.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-19: *"customer should be able to extend the cap using the status mail they
/// get, so they can extend from 50 -&gt; 60 as example. then i must get a email so i can extend it in
/// zoho + i can invoice, if it is a prepaid order"* … *"basically i want self service so customer can
/// see status, usage, extend and order more"*.</para>
///
/// <para>🔴 <b>THE REQUEST CHANGES NOTHING BY ITSELF, and it must not.</b> The limit that actually
/// stops a claim lives in Backstage, which has no coupon API (§787.14). A click that raised CEH's
/// number would leave the hub promising 60 while Zoho still refused at 50 — a lie told on the
/// partner's own page, by us. So this mails the operator, who raises it in both places.</para>
///
/// <para>🔑 <b>The mail states WHICH KIND of agreement it is, because that decides whether money
/// changes hands.</b> A prepaid extension is a purchase and needs an invoice; a capped ad-hoc
/// extension is just a larger agreed ceiling, billed as it is claimed. Same button for the partner,
/// two different jobs for him — so the mail does the telling rather than making him look it up.</para>
/// </remarks>
public sealed class CouponCapRequestNotifier
{
    private readonly EngineAlertSender _alerts;
    private readonly ILogger<CouponCapRequestNotifier>? _log;

    public CouponCapRequestNotifier(
        EngineAlertSender alerts, ILogger<CouponCapRequestNotifier>? log = null)
    {
        _alerts = alerts;
        _log = log;
    }

    /// <summary>What the partner is asking about — decides the wording and the money.</summary>
    public enum RequestKind
    {
        /// <summary>A capped ad-hoc coupon: raise the ceiling. No invoice up front.</summary>
        AdHocCap = 0,

        /// <summary>A prepaid pool: they want to buy more, so it ends in an invoice.</summary>
        PrepaidTopUp = 1,
    }

    /// <summary>Tell the ops mailbox that a partner wants more tickets.</summary>
    public async Task<bool> NotifyAsync(
        string customerLabel,
        int? erpCustomerNumber,
        RequestKind kind,
        int? currentCap,
        int requestedTotal,
        int claimedSoFar,
        string? note,
        CancellationToken ct = default,
        // §1096 — WHICH code. A customer can hold several, and "Arrow wants 60" is unactionable
        // without knowing which of their agreements it is about.
        string? couponName = null,
        // §1104 — what CEH has ALREADY DONE about it, or null when it only asked.
        string? appliedNote = null,
        // §1104 — WHO asked. 🔴 The monitor page is ANONYMOUS: the token identifies a customer, never
        // a person. So this is what they typed about themselves, and it is reported as exactly that
        // — an unverified claim — rather than presented as an identity CEH established.
        string? requestedByName = null,
        string? requestedByEmail = null,
        // The contact CEH already holds for this coupon, which is who the invoice is addressed to.
        string? contactOnFile = null)
    {
        if (requestedTotal <= 0) return false;

        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        static string N(int v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var isPrepaid = kind == RequestKind.PrepaidTopUp;

        // §1096 — the CODE is in the subject. A customer with two agreements produces two of these,
        // and without the code they read as duplicates of each other.
        var which = string.IsNullOrWhiteSpace(couponName) ? string.Empty : $" [{couponName}]";

        // 🔴 §1104 — LEAD WITH THE INCREASE, NOT THE TOTAL (operator 2026-08-20: *"focus on the extra
        // / extend amount"* · *"it must be clear what the amount is (increase)"*). "40 in total"
        // makes the reader do the subtraction to find the only number that matters — the 10 being
        // added, which is what gets invoiced and what the Backstage limit moves by.
        var extra = currentCap is { } cur && cur > 0 ? requestedTotal - cur : requestedTotal;

        var subject = isPrepaid
            ? $"Coupon{which}: {customerLabel} bought {N(extra)} more ticket(s) — {N(requestedTotal)} total"
            : $"Coupon{which}: {customerLabel} raised their cap by {N(extra)} — {N(requestedTotal)} total";

        // 🔴 §1104 — CEH HAS ALREADY DONE ITS HALF (operator 2026-08-20: *"if the customer chooses to
        // extend the amount, you have to increase the cap automatically"* · *"and if it is a prepaid,
        // it must create the invoice"* · *"why do i have to do that manually"*).
        //
        // ⚰️ This list used to ask him to raise the cap and record the purchase by hand — arithmetic
        // and data entry the hub already had. The ONE step that remains is the one CEH genuinely
        // cannot do: Backstage has no coupon API (§787.14).
        var whatToDo =
            "<li><strong>Raise the promo code's limit in Zoho Backstage to "
            + $"{N(requestedTotal)}</strong> &mdash; the only step left, and the only one CEH cannot "
            + "do for you.</li>";

        var body =
            $"<p><strong>{Enc(customerLabel)}</strong> has asked for more tickets from their own "
            + "status page.</p>"
            + "<table style=\"border-collapse:collapse;font-size:14px;\">"
            + Row("Customer", Enc(customerLabel)
                  + (erpCustomerNumber is > 0 ? $" (e-conomic {N(erpCustomerNumber.Value)})" : string.Empty))
            + (string.IsNullOrWhiteSpace(couponName)
                ? string.Empty
                : Row("Coupon / promo code", $"<strong>{Enc(couponName)}</strong>"))
            + Row("Agreement", isPrepaid ? "Prepaid pool" : "Ad-hoc with a cap")
            // §1104 — the INCREASE first and in bold; the total is context, not the headline.
            + Row(isPrepaid ? "Extra tickets bought" : "Cap raised by",
                  $"<strong style=\"font-size:16px;\">+{N(extra)}</strong>")
            + Row("Was → now",
                  $"{(currentCap is > 0 ? N(currentCap.Value) : "0")} &rarr; <strong>{N(requestedTotal)}</strong>")
            + Row("Claimed so far", N(claimedSoFar))
            + Row("Asked by",
                  string.IsNullOrWhiteSpace(requestedByName) && string.IsNullOrWhiteSpace(requestedByEmail)
                      ? "<em>not given &mdash; the page is anonymous, so this is only known if they type it</em>"
                      : $"{Enc(requestedByName)}{(string.IsNullOrWhiteSpace(requestedByEmail) ? string.Empty : $" &lt;{Enc(requestedByEmail)}&gt;")}"
                        + " <span style=\"color:#6b7280;\">(self-reported)</span>")
            + (string.IsNullOrWhiteSpace(contactOnFile)
                ? string.Empty
                : Row("Contact on file", Enc(contactOnFile)))
            + "</table>"
            + (string.IsNullOrWhiteSpace(note)
                ? string.Empty
                : $"<p style=\"font-size:14px;\"><em>Their note:</em> {Enc(note)}</p>")
            // §1104 — what the hub already did, before what he still has to.
            + (string.IsNullOrWhiteSpace(appliedNote)
                ? string.Empty
                : "<p style=\"background:#eef7ee;border-left:4px solid #1E7A3C;padding:10px 12px;"
                  + $"border-radius:4px;font-size:14px;\"><strong>Already done by CEH:</strong> {appliedNote}</p>")
            + $"<p style=\"font-size:14px;\"><strong>To do:</strong></p><ul style=\"font-size:14px;\">{whatToDo}</ul>"
            // 🔒 The same warning the whole coupon area carries: nothing here is enforced by CEH.
            + "<p style=\"color:#6b7280;font-size:13px;\">Until the limit is raised in Backstage the "
            + "customer cannot claim beyond the OLD one — CEH cannot change it, and cannot stop a "
            + "claim either. The hub now reports and invoices against the new number, so until you "
            + "raise it in Backstage the two disagree.</p>";

        await _alerts.AlertAsync(
            subject, body, ct, throttleKey: null,
            recipient: ZohoChangeNotifier.ActionableRecipient);

        _log?.LogInformation(
            "§1094: {Customer} requested {Requested} tickets ({Kind}); claimed {Claimed}, cap {Cap}.",
            customerLabel, requestedTotal, kind, claimedSoFar, currentCap);
        return true;

        static string Row(string label, string value) =>
            "<tr>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;color:#6b7280;\">{label}</td>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{value}</td>"
            + "</tr>";
    }
}
