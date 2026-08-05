using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §787 — hourly: claimed COUPON tickets → e-conomic DRAFT invoices. The replacement for
/// <c>Create-ERP-Invoice-Coupon-Tickets.ps1</c>.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Gated on <c>coupon-erp-invoicing</c>, which ships OFF</b>, and on
/// <c>Invoicing:DryRun</c>, which defaults TRUE (§787.4/§788). Deploying this changes nothing; going
/// live is two explicit acts, and the job says WHICH one is holding it — otherwise "no invoices
/// appeared" is indistinguishable from "broken" ([[ceh-two-switch-trap]]).</para>
///
/// <para>⚠️ <b>Unlike §786 there is no live behaviour to preserve.</b> The retired coupon script was
/// written ~Feb 2026 and has <b>never run</b> (§787.5), so it is evidence of intent, not a
/// specification — its field assumptions were re-verified against the live order mirror (§787.6)
/// rather than trusted.</para>
///
/// <para>Runs at :50, clear of both the order sync and the §786 webshop invoicing at :40, so a pass
/// reads orders that have finished landing.</para>
/// </remarks>
public sealed class CouponInvoiceJob
{
    /// <summary>🔒 A live DB key — renaming it silently DISABLES the job.</summary>
    public const string FeatureKey = "coupon-erp-invoicing";

    private readonly CommunityHubDbContext _db;
    private readonly CouponDraftInvoiceService _invoicing;
    private readonly CouponMappingAlertService _alerts;
    private readonly CouponDiscoveryService _discovery;
    private readonly CouponPrepaidBillingReminderService _prepaidReminders;
    private readonly CouponPrepaidLowBalanceAlertService _lowBalance;
    private readonly DraftInvoiceCreatedNotifier _draftNotices;
    private readonly InvoiceProblemNotifier _problems;
    private readonly FeatureGateService _gate;
    private readonly ILogger<CouponInvoiceJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public CouponInvoiceJob(
        CommunityHubDbContext db,
        CouponDraftInvoiceService invoicing,
        CouponMappingAlertService alerts,
        CouponDiscoveryService discovery,
        CouponPrepaidBillingReminderService prepaidReminders,
        CouponPrepaidLowBalanceAlertService lowBalance,
        DraftInvoiceCreatedNotifier draftNotices,
        InvoiceProblemNotifier problems,
        FeatureGateService gate,
        ILogger<CouponInvoiceJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db;
        _invoicing = invoicing;
        _alerts = alerts;
        _discovery = discovery;
        _prepaidReminders = prepaidReminders;
        _lowBalance = lowBalance;
        _draftNotices = draftNotices;
        _problems = problems;
        _gate = gate;
        _log = log;
        _activity = activity;
    }

    [Function("CouponInvoiceJob")]
    // §878 — BASE TICK ONLY; the cadence is the operator's interval on /Organizer/Jobs.
    // 🔒 Safe to run often: a coupon already invoiced is skipped, and one with no mapped
    // e-conomic customer is reported rather than guessed at.
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);

        if (eventId is null)
        {
            _activity?.ReportInactive("No active edition, so there are no coupon tickets to invoice.");
            return;
        }

        // 🔴 §797 — DISCOVERY RUNS FIRST, AND BEFORE THE GATE.
        //
        // §787 already auto-creates an Unmapped row, but only from inside the invoicing sweep — and
        // measured on PROD 2026-08-04 that path never reaches it: every coupon claim in the mirror
        // is CANCELLED, so the sweep returns at `claims.Count == 0` before the auto-create block and
        // the coupon stays invisible for ever. A cancelled claim still proves the code exists, and
        // the next claim on it is billable.
        //
        // 🔒 Before the gate for the same reason as §795.2/§796: discovering that a coupon exists is
        // not the act of writing an invoice.
        await _discovery.DiscoverAsync(eventId.Value, ct);

        // 🔴 §795.2 — THE PREPAID CHASE RUNS BEFORE THE FEATURE GATE, DELIBERATELY.
        //
        // `coupon-erp-invoicing` governs whether CEH WRITES draft invoices to e-conomic. A PREPAID
        // pool is precisely the billing type CEH never writes an invoice for (§794) — so putting the
        // chase behind that switch would mean the one billing type nobody invoices is also the one
        // nobody is ever reminded about, which is the hole §795.2 exists to close.
        //
        // 🔒 Its real gate is the DATA: a pool only exists because a human created it on
        // /Organizer/CouponInvoicing, it is left alone for 24h, and it goes quiet for a week after
        // each reminder — and permanently the moment a number is entered.
        await _prepaidReminders.RemindAsync(eventId.Value, ct);

        // 🔴 §796 — SO DOES THE LOW-BALANCE WARNING, and here the reason is sharper still: CEH
        // cannot stop a claim. Backstage has no coupon API (§787.14), so an exhausted pool leaves
        // the promo code working and the next claimant takes a ticket nobody paid for. A warning
        // held behind an invoicing switch would arrive after the money was already gone.
        await _lowBalance.AlertAsync(eventId.Value, ct);

        if (!await _gate.IsFeatureEnabledAsync(FeatureKey, eventId.Value, ct))
        {
            // Named, not silent: OFF is the correct shipped state, and a reader must be able to tell
            // that apart from a job that is failing.
            _activity?.ReportInactive(
                "The 'coupon-erp-invoicing' switch is off, so no coupon draft invoices are created. "
                + "This is the shipped default — turn it on when the coupon mappings are in place on "
                + "Organizer → Setup → Coupon invoicing.");
            return;
        }

        var result = await _invoicing.RunAsync(eventId.Value, ct);

        // 🔴 The alert fires even in a DRY RUN, and that is deliberate: an unmapped coupon is a
        // mapping problem, not a writing problem. Dry run governs whether CEH may WRITE to
        // e-conomic; it must not govern whether a human is told that a ticket cannot be billed.
        if (result.UnmappedCoupons.Count > 0)
        {
            await _alerts.AlertAsync(eventId.Value, result.UnmappedCoupons, ct);
        }

        // §795.3 — *"notify info also when new invoices was created in draft"*. A draft reaches
        // nobody until a human books it, so an unannounced draft is invisible revenue.
        // 🔒 Silent when nothing was created, and empty by construction in a dry run — the service
        // only fills CreatedDrafts after e-conomic accepts a POST.
        await _draftNotices.NotifyAsync("Coupon invoicing", result.CreatedDraftsOrEmpty, ct);

        _activity?.ReportExamined(result.ClaimsSeen, "claimed coupon ticket(s)");

        // 🔴 §813 — the refusals reach a human. A coupon claim nobody can bill is a ticket
        // somebody is attending for free, and until now the reason lived only in the log.
        await _problems.NotifyAsync("Coupon invoicing", result.Problems, ct);

        foreach (var problem in result.Problems)
        {
            _log.LogWarning("CouponInvoiceJob: {Problem}", problem);
        }

        if (result.DryRun)
        {
            foreach (var would in result.WouldCreateOrEmpty)
            {
                _log.LogInformation("CouponInvoiceJob DRY RUN would create: {Would}", would);
            }

            // §788 — say which switch is holding it. "Nothing appeared" has three possible causes,
            // and a run that does not name its own is the trap this comment exists to prevent.
            _activity?.ReportInactive(
                $"DRY RUN: {result.ClaimsSeen} claim(s) examined, "
                + $"{result.WouldCreateOrEmpty.Count} invoice(s) WOULD have been created, but "
                + "Invoicing:DryRun is true so nothing was written to e-conomic. The feature switch "
                + "is ON — it is the dry run holding this, not the switch.");
        }
    }
}
