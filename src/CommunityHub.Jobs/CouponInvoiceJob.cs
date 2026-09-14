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
    private readonly CouponCustomerMonitorProvisioner? _monitors;
    private readonly CouponUsageReportService? _usageReports;
    private readonly Microsoft.Extensions.Configuration.IConfiguration? _config;
    private readonly CommunityHub.Core.Integrations.TestModeOptions? _testMode;

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
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null,
        CouponCustomerMonitorProvisioner? monitors = null,
        CouponUsageReportService? usageReports = null,
        Microsoft.Extensions.Configuration.IConfiguration? config = null,
        // 🔴 §1119 — the host's own answer to "can I create an invoice at all?".
        CommunityHub.Core.Integrations.TestModeOptions? testMode = null)
    {
        _testMode = testMode;
        _usageReports = usageReports;
        _config = config;
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
        _monitors = monitors;
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

        // 🔴 §1093 — EVERY BILLING CUSTOMER GETS ITS MONITOR LINK, AND THIS RUNS BEFORE THE GATE TOO.
        //
        // Same reasoning as the three above: `coupon-erp-invoicing` governs whether CEH WRITES
        // invoices to e-conomic. Giving a partner a read-only view of their own usage is not that,
        // and it is most useful precisely while invoicing is still switched off — that is the window
        // where nobody has any other way to see what a code is doing.
        //
        // 🔒 Idempotent and it never resurrects a revoked link, so running it every tick is free and
        // cannot undo an organizer's decision.
        //
        // ⚠️ No customer-name resolver here on purpose: naming would mean an e-conomic call per new
        // customer inside a 5-minute timer, and a monitor named "customer 1234" that EXISTS beats a
        // prettier one that does not. The page can rename it.
        if (_monitors is not null)
        {
            try
            {
                await _monitors.EnsureAsync(eventId.Value, nameFor: null, ct);
            }
            catch (Exception ex)
            {
                // Fail-soft: a missing link must never stop the invoicing sweep behind it.
                _log.LogWarning(ex, "§1093: coupon-customer monitor provisioning threw.");
            }
        }

        // §1094 — the partner's fortnightly status mail. Also before the gate: telling a customer
        // what they have used is not writing an invoice, and it is most wanted precisely while
        // invoicing is off. Its own 14-day stamp is the cadence; this tick just asks "anything due?".
        if (_usageReports is not null)
        {
            try
            {
                // 🔑 The SAME base-URL resolution AttendeeBackstageSyncJob uses. A second convention
                // for "what is our public address" is how one mail ends up linking to a host that
                // does not answer — and this mail is nothing but a link.
                var domain = _config?["Hub:CustomDomain"];
                var baseUrl = string.IsNullOrWhiteSpace(domain)
                    ? "https://eldk27.eventhub.expertslive.dk"
                    : $"https://{domain}";

                var sent = await _usageReports.SendDueAsync(eventId.Value, baseUrl, ct);
                if (sent > 0)
                    _log.LogInformation("§1094: sent {Count} partner usage report(s).", sent);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "§1094: partner usage reports threw.");
            }
        }

        // 🔴 §1119 — A HOST THAT CANNOT CREATE AN INVOICE MUST NOT RUN THE SWEEP, AND MUST NOT MAIL.
        //
        // Operator 2026-08-21, having said it EIGHT TIMES: *"i still keep seeing this from dev env.
        // as I told 8 times, no erp create of invoice from dev. this can only happend on prod"*.
        //
        // ⚠️ <b>My previous answer was true and useless.</b> I explained that TestMode swaps in
        // `TestModeEconomicInvoiceClient` and dry run is on, so nothing reached e-conomic — which is
        // correct, and is not what he asked for. He is not receiving an invoice; he is receiving a
        // MAIL from DEV about production billing, every time the list changes, and that is the thing
        // he told me to stop. Explaining the mechanism again is not a fix.
        //
        // 🔑 §1059 already settled the shape for exactly this, one job over: *"THIS JOB IS the
        // ERP→webshop sync: every effect it has is a webshop write. If those are forbidden it has no
        // work to do, so the honest thing is not to start."* The invoicing sweep is the same — its
        // every effect is an e-conomic draft or a mail about one — and it deserved the same rule.
        //
        // 🔒 TestMode is the right signal, not a new setting: it is ALREADY what decides that this
        // host gets `TestModeEconomicInvoiceClient` instead of the live one. A host that has been
        // handed a fake invoice client has, by definition, no invoices to create and nothing to
        // report about them.
        //
        // ⚠️ Deliberately AFTER the four pre-gate steps above. §795.2/§796/§1093/§1094 each argue at
        // length that they are not invoicing — a prepaid chase, a low-balance warning, a read-only
        // monitor link, a usage report — and their mails redirect to the operator in DEV anyway.
        // This stops the invoicing, not the whole job.
        if (_testMode?.Enabled == true)
        {
            _activity?.ReportInactive(
                "TestMode is on, so this host cannot create e-conomic invoices — the coupon "
                + "invoicing sweep does not run and reports nothing. Invoicing happens on PROD only.");
            _log.LogInformation(
                "§1119 CouponInvoiceJob: TestMode is on — invoicing sweep skipped, no problem mail.");
            return;
        }

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
