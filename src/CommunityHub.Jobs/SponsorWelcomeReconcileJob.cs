using CommunityHub.Core.Data;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// DESIRED-STATE sponsor welcome (operator 2026-06-23). Every 15 minutes, ensure
/// every sponsor company's EVENT-COORDINATOR contacts who are (a) inside the
/// <c>welcome-email</c> feature's released ring and (b) not yet welcomed actually
/// receive the welcome — no manual "Resend" click required. This mirrors the
/// attendee auto-welcome on the Backstage pull.
///
/// Correctness: <see cref="WelcomeEmailService"/> now skips (without recording) a
/// recipient outside the released ring, so when the organizer WIDENS the ring the
/// newly-in-scope coordinators are welcomed on the next run. Idempotent via the
/// SentReminder ledger; the sponsor provisioning dependency guard still applies.
/// </summary>
public sealed class SponsorWelcomeReconcileJob
{
    private readonly SponsorWelcomeEmailService _welcome;
    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly ILogger<SponsorWelcomeReconcileJob> _log;
    // §545(b) — optional, so an un-instrumented job simply says nothing (silence = UNKNOWN).
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public SponsorWelcomeReconcileJob(
        SponsorWelcomeEmailService welcome,
        CommunityHubDbContext db,
        FeatureGateService gate,
        ILogger<SponsorWelcomeReconcileJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _welcome = welcome;
        _db = db;
        _gate = gate;
        _log = log;
        _activity = activity;
    }

    [Function("SponsorWelcomeReconcileJob")]
    public async Task Run([TimerTrigger("0 */15 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogInformation("SponsorWelcomeReconcileJob: no active edition; skipped.");
            _activity?.ReportInactive(
                "There is no ACTIVE edition, so no sponsor welcome can be reconciled.");
            return;
        }

        // Only reconcile while the welcome-email feature is enabled for the edition
        // (the per-recipient ring is enforced inside WelcomeEmailService).
        if (!await _gate.IsFeatureEnabledAsync("welcome-email", eventId.Value, ct))
        {
            _log.LogInformation("SponsorWelcomeReconcileJob: welcome-email disabled; skipped.");
            _activity?.ReportInactive(
                "The 'welcome-email' feature is switched off, so no sponsor contact is being "
                + "welcomed — including anyone added since it was turned off.");
            return;
        }

        _activity?.ReportWork();

        var results = await _welcome.SendForAllSponsorsAsync(eventId.Value, ct);
        var sent = results.Sum(r => r.Sent);
        var blocked = results.Count(r => r.Blocked);
        if (sent > 0 || blocked > 0)
        {
            _log.LogInformation(
                "SponsorWelcomeReconcileJob: sent {Sent} welcome(s) across {Co} companies; "
                + "{Blocked} company(ies) awaiting SharePoint provisioning.",
                sent, results.Count, blocked);
        }
    }
}
