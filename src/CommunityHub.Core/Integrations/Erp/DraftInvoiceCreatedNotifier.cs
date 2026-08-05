using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §795.3 — one draft invoice CEH actually created in e-conomic.
/// </summary>
/// <param name="Description">What it is FOR, in the words the operator uses ("Coupon 'ARROW-DK'", "Webshop order 4711").</param>
/// <param name="CustomerNumber">The e-conomic customer that will be billed.</param>
/// <param name="CustomerName">That customer's name, so the mail can be read without a lookup.</param>
/// <param name="Reference">The <c>references.other</c> marker — the string that finds it again.</param>
/// <param name="Total">The invoice total, in <paramref name="Currency"/>.</param>
/// <param name="Currency">The currency the draft was raised in (NOT necessarily the source currency).</param>
/// <param name="DraftNumber">
/// e-conomic's <c>draftInvoiceNumber</c>. ⚠️ PROVISIONAL — it is replaced by a booked invoice number
/// the moment a human books it (§795.4), so it is quoted here as a draft and never as an invoice
/// number a partner would recognise.
/// </param>
public sealed record CreatedDraftInvoice(
    string Description,
    int CustomerNumber,
    string CustomerName,
    string Reference,
    decimal Total,
    string Currency,
    int DraftNumber);

/// <summary>
/// §795.3 — mails <c>info@</c> when CEH creates DRAFT invoices.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"notify info also when new invoices was created in draft"*.</para>
///
/// <para>🔑 <b>A draft reaches nobody until a human books it.</b> That is the thread running through
/// all of §786/§787: the invoice exists, the money is owed, and nobody is waiting for payment —
/// because nobody knows it is there. Drafts created and never booked are invisible revenue, and this
/// mail is what turns a draft into work somebody has.</para>
///
/// <para>🔒 <b>Silent when nothing was created</b> (§302) and <b>silent in a DRY RUN</b> — a dry run
/// creates nothing, so announcing it would name invoices that do not exist. Both are enforced at the
/// CALL SITE by only ever passing drafts that were really created: this class cannot tell the
/// difference, and a would-create list must never reach it.</para>
///
/// <para>🔒 It goes to the ACTIONABLE mailbox (§556) rather than the developer one: booking an
/// invoice is a bookkeeping act, and more than one person must be able to do it.</para>
/// </remarks>
public sealed class DraftInvoiceCreatedNotifier
{
    private readonly EngineAlertSender _alerts;
    private readonly ILogger<DraftInvoiceCreatedNotifier>? _log;

    public DraftInvoiceCreatedNotifier(
        EngineAlertSender alerts, ILogger<DraftInvoiceCreatedNotifier>? log = null)
    {
        _alerts = alerts;
        _log = log;
    }

    /// <summary>
    /// Announce the drafts one pass created. Returns false — and sends nothing — when the list is
    /// empty.
    /// </summary>
    /// <param name="source">Which sweep created them ("Coupon invoicing", "Webshop orders").</param>
    public async Task<bool> NotifyAsync(
        string source, IReadOnlyList<CreatedDraftInvoice> drafts, CancellationToken ct = default)
    {
        if (drafts is null || drafts.Count == 0) return false;

        static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

        var rows = string.Join("", drafts.Select(d =>
            "<tr>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{Enc(d.Description)}</td>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">{Enc(d.CustomerName)} ({d.CustomerNumber})</td>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;text-align:right;\">{d.Total:0.##} {Enc(d.Currency)}</td>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;\">draft {d.DraftNumber}</td>"
            + $"<td style=\"padding:6px 10px;border-bottom:1px solid #e5e7eb;color:#6b7280;\">{Enc(d.Reference)}</td>"
            + "</tr>"));

        var total = drafts.Sum(d => d.Total);
        var currencies = drafts.Select(d => d.Currency).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var totalLine = currencies.Count == 1
            ? $"<p><strong>{total:0.##} {Enc(currencies[0])}</strong> in total.</p>"
            // ⚠️ Never add across currencies — a summed EUR+DKK figure would be a number that means
            // nothing, printed with authority.
            : string.Empty;

        var body =
            $"<p><strong>CEH created {drafts.Count} draft invoice(s) in e-conomic ({Enc(source)}).</strong></p>"
            + "<table style=\"border-collapse:collapse;font-size:14px;\">"
            + "<tr>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">For</th>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Customer</th>"
            + "<th style=\"text-align:right;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Amount</th>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Draft</th>"
            + "<th style=\"text-align:left;padding:6px 10px;border-bottom:2px solid #d1d5db;\">Reference</th>"
            + "</tr>"
            + rows
            + "</table>"
            + totalLine
            + "<p style=\"color:#6b7280;font-size:13px;\">These are <strong>drafts</strong>. They "
            + "reach nobody until somebody books them in e-conomic — an unbooked draft is an invoice "
            + "the customer never receives and nobody is chasing. The draft number changes to a real "
            + "invoice number when it is booked.</p>";

        // 🔒 throttleKey null. This is a "records created" notification, not a recurring condition:
        // suppressing a second batch because a first one was announced 20 minutes ago would lose
        // invoices, and each mail names different drafts.
        await _alerts.AlertAsync(
            $"e-conomic: {drafts.Count} new draft invoice(s) created ({source})",
            body, ct, throttleKey: null,
            recipient: ZohoChangeNotifier.ActionableRecipient);

        _log?.LogInformation(
            "§795.3: announced {Count} newly created draft invoice(s) from {Source}.",
            drafts.Count, source);
        return true;
    }
}
