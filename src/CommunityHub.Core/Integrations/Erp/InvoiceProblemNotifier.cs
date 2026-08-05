using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// §813 — tells a human when an order or a coupon claim CANNOT be invoiced.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Since the cutover, a skipped order is invisible revenue.</b> §786.4: the VM script is
/// stopped and is NOT a fallback, so an order CEH refuses is invoiced by nobody. Both services
/// already refuse with a named, human reason — *"company 'X' has no erp_customer_number set in
/// Company Manager"*, *"no usable EUR->DKK rate"* — but those reasons only reached the LOG, and the
/// log is not a place anybody looks. This is the §795.3 argument applied to the other half: a draft
/// nobody knows about is invisible revenue, and so is an invoice that was never created at all.</para>
///
/// <para>🔒 <b>Silent when nothing is stuck</b> (§302), and it goes to the DEVELOPER mailbox because
/// it is an ISSUE, not a queue item (§808: *"alert mails (issues) … goes to mok@expertslive.dk"*).</para>
///
/// <para>⚠️ <b>It fires in a dry run too</b>, deliberately — the same rule §787's unmapped-coupon
/// alert follows. Dry run governs whether CEH may WRITE to e-conomic; it must never govern whether a
/// human is told that an order cannot be billed.</para>
/// </remarks>
public sealed class InvoiceProblemNotifier
{
    private readonly EngineAlertSender _alerts;
    private readonly ILogger<InvoiceProblemNotifier>? _log;

    public InvoiceProblemNotifier(EngineAlertSender alerts, ILogger<InvoiceProblemNotifier>? log = null)
    {
        _alerts = alerts;
        _log = log;
    }

    /// <summary>
    /// 🔑 §813.1 — the throttle key is derived from the PROBLEMS THEMSELVES, not from the job name.
    /// </summary>
    /// <remarks>
    /// <para>The same stuck order every hour must not mail every hour (§302) — but a NEW stuck order
    /// must not wait for someone else's quiet period to expire. Hashing the sorted problem set gives
    /// both: an unchanged set keeps the same key and stays suppressed inside
    /// <c>EngineAlertSender</c>'s window, while one new problem changes the key and alerts on the
    /// next pass. It is §796's "deterioration breaks the quiet period" without needing a table.</para>
    ///
    /// <para>⚠️ The throttle lives in the singleton's memory, so a host restart (a deploy) can let one
    /// repeat through. That is the right trade here: a duplicate mail costs a glance, a missed one
    /// costs an invoice.</para>
    /// </remarks>
    internal static string ThrottleKeyFor(IEnumerable<string> problems)
    {
        var payload = string.Join("\n", problems.OrderBy(p => p, StringComparer.Ordinal));
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(payload)))[..16];
        return $"invoice-problems:{hash}";
    }

    /// <summary>
    /// Report what could not be invoiced. Returns false — and sends nothing — when the list is empty.
    /// </summary>
    /// <param name="source">Which sweep produced them ("Webshop orders", "Coupon invoicing").</param>
    public async Task<bool> NotifyAsync(
        string source, IReadOnlyList<string> problems, CancellationToken ct = default)
    {
        if (problems is null || problems.Count == 0) return false;

        var items = string.Join("", problems.Select(p =>
            $"<li>{System.Net.WebUtility.HtmlEncode(p)}</li>"));

        var body =
            $"<p><strong>{problems.Count} item(s) could not be invoiced ({System.Net.WebUtility.HtmlEncode(source)}).</strong></p>"
            + $"<ul>{items}</ul>"
            + "<p style=\"background:#fff4e5;border-left:4px solid #d97706;padding:10px 12px;"
            + "border-radius:4px;\"><strong>Nothing else is going to invoice these.</strong> The "
            + "scheduled PowerShell script was retired, so CEH is the only system creating these "
            + "drafts — an order that keeps being refused is money nobody is asking for.</p>"
            + "<p style=\"color:#6b7280;font-size:13px;\">Each line above says exactly what is "
            + "missing, and most are fixed outside CEH: an ERP customer number in Company Manager, a "
            + "price on the webshop product, or a billing rule on "
            + "<strong>Organizer → Setup → Coupon invoicing</strong>. This repeats only when the list "
            + "CHANGES — a new problem is reported on the next run rather than waiting for a quiet "
            + "period to expire.</p>";

        // §808 — an ISSUE, so the developer mailbox (EngineAlertSender's default), not info@.
        await _alerts.AlertAsync(
            $"ACTION REQUIRED: {problems.Count} item(s) cannot be invoiced ({source})",
            body, ct, throttleKey: ThrottleKeyFor(problems));

        _log?.LogInformation(
            "§813: reported {Count} un-invoiceable item(s) from {Source}.", problems.Count, source);
        return true;
    }
}
