using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// DESIRED-STATE welcome for the NON-sponsor roles (operator 2026-06-23). Every 10
/// minutes, ensure every welcome-eligible participant who is (a) inside the
/// <c>welcome-email</c> feature's released ring and (b) not yet welcomed actually
/// receives their role welcome — no manual "Resend" click required.
///
/// Scope: Speaker / Volunteer / Media / Event-partner. Sponsors are handled by the
/// guarded <see cref="SponsorWelcomeReconcileJob"/> (it waits for SharePoint
/// provisioning), and Organizers + Attendees get no platform welcome
/// (<see cref="CommunityHub.Core.Email.WelcomeVariants.TemplateKeyFor"/>), so both
/// are excluded here.
///
/// Correctness: <see cref="WelcomeEmailService.SendWelcomeAsync"/> is idempotent via
/// the SentReminder ledger AND skips (without recording) a recipient outside the
/// released ring — so when the organizer WIDENS the ring the newly-in-scope people
/// are welcomed on the next run. The per-recipient ring is enforced inside the
/// service; this job only gates on the edition-level kill switch.
/// </summary>
public sealed class WelcomeReconcileJob
{
    // Welcome-eligible roles EXCLUDING Sponsor (own guarded reconcile) and the
    // no-welcome roles Organizer + Attendee.
    private static readonly ParticipantRole[] Roles =
    {
        ParticipantRole.Speaker, ParticipantRole.Volunteer,
        ParticipantRole.Media, ParticipantRole.EventPartner,
    };

    private readonly WelcomeEmailService _welcome;
    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly ILogger<WelcomeReconcileJob> _log;

    // §340-B: this is a BULK send loop and was the only one without the §219 pacer.
    // Optional so existing constructions/tests are unchanged (null ⇒ no delay).
    private readonly IBulkSendPacer? _pacer;
    // §545(b) — optional, so an un-instrumented job simply says nothing (silence = UNKNOWN).
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public WelcomeReconcileJob(
        WelcomeEmailService welcome,
        CommunityHubDbContext db,
        FeatureGateService gate,
        ILogger<WelcomeReconcileJob> log,
        IBulkSendPacer? pacer = null,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _welcome = welcome;
        _db = db;
        _gate = gate;
        _log = log;
        _pacer = pacer;
        _activity = activity;
    }

    [Function("WelcomeReconcileJob")]
    public async Task Run([TimerTrigger("0 */10 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogInformation("WelcomeReconcileJob: no active edition; skipped.");
            _activity?.ReportInactive(
                "There is no ACTIVE edition, so nobody is being welcomed.");
            return;
        }

        // Only reconcile while the welcome-email feature is enabled for the edition
        // (the per-recipient ring is enforced inside WelcomeEmailService).
        if (!await _gate.IsFeatureEnabledAsync("welcome-email", eventId.Value, ct))
        {
            _log.LogInformation("WelcomeReconcileJob: welcome-email disabled; skipped.");
            // This is the reconcile that catches up when a ring WIDENS, so while it is off, new
            // participants accumulate unwelcomed and nothing says so.
            _activity?.ReportInactive(
                "The 'welcome-email' feature is switched off, so no participant is being welcomed "
                + "— new sign-ups are accumulating unwelcomed.");
            return;
        }

        _activity?.ReportWork();

        var ids = await _db.Participants
            .Where(p => p.EventId == eventId.Value && p.IsActive && Roles.Contains(p.Role))
            .Select(p => p.Id)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var id in ids)
        {
            try
            {
                if (await _welcome.SendWelcomeAsync(id, ct))
                {
                    sent++;
                    // §340-B PACING: space the bulk loop under Brevo's rate limit. Paced
                    // AFTER an actual send, not before every iteration (the sibling loops in
                    // AttendeeBackstageSyncJob pace up-front because every iteration there IS
                    // a send). Here the overwhelming majority of iterations are idempotent
                    // skips of already-welcomed people, so an up-front delay would add
                    // ~150 ms × every participant on a job that runs every 10 minutes, for
                    // nothing. This shape delays only between real outbound mail.
                    if (_pacer is not null) await _pacer.PaceAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WelcomeReconcileJob: failed to welcome participant {Id}.", id);
            }
        }

        if (sent > 0)
        {
            _log.LogInformation(
                "WelcomeReconcileJob: sent {Sent} welcome(s) across {N} eligible participant(s).",
                sent, ids.Count);
        }
    }
}
