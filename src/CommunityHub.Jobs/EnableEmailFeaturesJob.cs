using CommunityHub.Core.Data;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// ONE-OFF (admin-triggered) enable of the ring-1 email-template features for the
/// active edition (operator 2026-06-23). Advanced features default OFF by design
/// (a deploy must never auto-send mail), so this is the explicit, deliberate
/// per-edition opt-in the organizer would otherwise do in Settings → Features —
/// done programmatically because the operator asked. Idempotent; each feature is
/// already released to Ring 1, so enabling only flips the kill switch on.
///
/// Scheduled yearly purely so it never fires unattended between deploys; it is
/// meant to be invoked once via POST /admin/functions/EnableEmailFeaturesJob.
/// </summary>
public sealed class EnableEmailFeaturesJob
{
    // The ring-1 email-template gates (NOT sponsor-leads, which is Broad/unscoped).
    // magic-link first so invitation-email's dependency is satisfied.
    private static readonly string[] EmailFeatures =
    {
        "welcome-email", "magic-link", "masterclass-invites", "reminder-jobs",
        "digest-emails", "session-eval-email", "invitation-email", "broadcast-email",
        "onboarding-step-reset", "travel-reimbursement-email", "group-photo-invites",
        "sponsor-reminders",
    };

    private readonly FeatureSettingsService _settings;
    private readonly CommunityHubDbContext _db;
    private readonly ILogger<EnableEmailFeaturesJob> _log;

    public EnableEmailFeaturesJob(
        FeatureSettingsService settings, CommunityHubDbContext db, ILogger<EnableEmailFeaturesJob> log)
    {
        _settings = settings;
        _db = db;
        _log = log;
    }

    [Function("EnableEmailFeaturesJob")]
    public async Task Run([TimerTrigger("0 0 0 1 1 *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("EnableEmailFeaturesJob: no active edition; nothing enabled.");
            return;
        }

        var enabled = 0;
        foreach (var key in EmailFeatures)
        {
            try
            {
                await _settings.SetEnabledAsync(eventId.Value, key, true, "system-bootstrap", ct);
                enabled++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "EnableEmailFeaturesJob: failed to enable {Key}.", key);
            }
        }

        _log.LogInformation(
            "EnableEmailFeaturesJob: enabled {N} email-template feature(s) for edition {E} (Ring 1).",
            enabled, eventId);
    }
}
