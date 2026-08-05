using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §786 — hourly: completed webshop orders → e-conomic DRAFT invoices. The replacement for
/// <c>Sync-Webshop-Orders-Create-ERP-Invoice.ps1</c>, which the operator runs hourly on a VM
/// (<i>"I need this script to be migrated into ceh, so i can retire the script"</i>).
/// </summary>
/// <remarks>
/// <para>🔒 <b>Gated on <c>webshop-erp-invoicing</c>, which ships OFF.</b> Merging and deploying this
/// changes nothing; turning the switch on IS the cutover, and it is the operator's to make. The same
/// pattern retired the ERP contacts script (<c>erp-webshop-reconcile</c>).</para>
///
/// <para>⚠️ <b>The order of the cutover matters:</b> switch the scheduled script OFF first, then turn
/// this on. Both running is survivable — they share the <c>WebshopOrderId-&lt;n&gt;</c> marker, so
/// the second to run skips — but "both on" is a race that relies on the interlock rather than a
/// plan, and it makes the first wrong invoice much harder to explain. See §786.2.</para>
///
/// <para>Runs at :40 past the hour, deliberately clear of the 15-minute webshop order pull, so a
/// pass reads orders that have finished landing rather than half of them.</para>
/// </remarks>
public sealed class WebshopInvoiceJob
{
    /// <summary>🔒 A live DB key — renaming it silently DISABLES the job.</summary>
    public const string FeatureKey = "webshop-erp-invoicing";

    private readonly CommunityHubDbContext _db;
    private readonly WebshopDraftInvoiceService _invoicing;
    private readonly DraftInvoiceCreatedNotifier _draftNotices;
    private readonly InvoiceProblemNotifier _problems;
    private readonly FeatureGateService _gate;
    private readonly ILogger<WebshopInvoiceJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public WebshopInvoiceJob(
        CommunityHubDbContext db,
        WebshopDraftInvoiceService invoicing,
        DraftInvoiceCreatedNotifier draftNotices,
        InvoiceProblemNotifier problems,
        FeatureGateService gate,
        ILogger<WebshopInvoiceJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db;
        _invoicing = invoicing;
        _draftNotices = draftNotices;
        _problems = problems;
        _gate = gate;
        _log = log;
        _activity = activity;
    }

    [Function("WebshopInvoiceJob")]
    // §878 — BASE TICK ONLY; the cadence is the operator's interval on /Organizer/Jobs.
    // 🔒 Safe to run often: an order already on any draft or booked invoice is skipped, and one
    // that cannot be priced is reported rather than guessed at — so it never double-invoices.
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);

        if (eventId is null)
        {
            _activity?.ReportInactive("No active edition, so there are no webshop orders to invoice.");
            return;
        }

        if (!await _gate.IsFeatureEnabledAsync(FeatureKey, eventId.Value, ct))
        {
            // Named, not silent: while the scheduled PowerShell script still owns this, the switch
            // being off is the CORRECT state — and a reader must be able to tell that apart from a
            // job that is broken.
            _activity?.ReportInactive(
                "The 'webshop-erp-invoicing' switch is off, so no draft invoices are created. This is "
                + "the expected state until the scheduled PowerShell script is retired — turn the "
                + "script off first, then turn this on.");
            return;
        }

        var result = await _invoicing.RunAsync(ct);

        if (result.Created > 0 || result.Skipped > 0 || result.WouldCreateOrEmpty.Count > 0)
        {
            _activity?.ReportWork();
        }

        if (result.DryRun)
        {
            // §788 — named loudly. "0 created" during a dry run must never read as "there was
            // nothing to do": those are opposite facts with the same number.
            _log.LogInformation(
                "§788 WebshopInvoiceJob: DRY RUN — nothing was written. {Seen} order(s) in scope, "
                + "{Already} already invoiced, {Would} would have been created, {Skipped} skipped. "
                + "Set Invoicing:DryRun=false when you want these created for real.",
                result.OrdersSeen, result.AlreadyInvoiced,
                result.WouldCreateOrEmpty.Count, result.Skipped);

            foreach (var would in result.WouldCreateOrEmpty)
            {
                _log.LogInformation("§788 WebshopInvoiceJob WOULD CREATE: {Invoice}", would);
            }
        }
        else
        {
            _log.LogInformation(
                "§786 WebshopInvoiceJob: {Seen} order(s) in scope, {Already} already invoiced, "
                + "{Created} draft(s) created, {Skipped} skipped.",
                result.OrdersSeen, result.AlreadyInvoiced, result.Created, result.Skipped);
        }

        // §795.3 — the same notice as the coupon job, for the same reason: a draft nobody knows
        // about is an invoice the customer never receives. Silent when nothing was created, and in
        // a dry run nothing ever is.
        await _draftNotices.NotifyAsync("Webshop orders", result.CreatedDraftsOrEmpty, ct);

        // 🔴 §813 — and the skips now reach a HUMAN, not just the log. Since the cutover the VM
        // script is stopped and is not a fallback (§786.4), so an order CEH refuses is invoiced by
        // nobody. The reasons were always written; nobody was reading them.
        await _problems.NotifyAsync("Webshop orders", result.Problems, ct);

        // 🔒 Every skip is reported with its reason. The retired script e-mailed the operator on each
        // failure; the hub's equivalent is the job activity record + the log, and a skipped order is
        // an invoice a sponsor never receives — the exact thing that must not be quiet.
        foreach (var problem in result.Problems)
        {
            _log.LogWarning("§786 WebshopInvoiceJob: {Problem}", problem);
        }
    }
}
