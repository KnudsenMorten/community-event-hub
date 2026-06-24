using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
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

    public WelcomeReconcileJob(
        WelcomeEmailService welcome,
        CommunityHubDbContext db,
        FeatureGateService gate,
        ILogger<WelcomeReconcileJob> log)
    {
        _welcome = welcome;
        _db = db;
        _gate = gate;
        _log = log;
    }

    [Function("WelcomeReconcileJob")]
    public async Task Run([TimerTrigger("0 */10 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogInformation("WelcomeReconcileJob: no active edition; skipped.");
            return;
        }

        // Only reconcile while the welcome-email feature is enabled for the edition
        // (the per-recipient ring is enforced inside WelcomeEmailService).
        if (!await _gate.IsFeatureEnabledAsync("welcome-email", eventId.Value, ct))
        {
            _log.LogInformation("WelcomeReconcileJob: welcome-email disabled; skipped.");
            return;
        }

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
