using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// Pulls Backstage attendees (v3, enriched: ticket id + contact + all custom fields
/// + order company/country/tax) and syncs them into CEH KEYED ON THE TICKET ID
/// (REQUIREMENTS §6). A reassigned ticket transfers its Master Class to the new
/// holder (who is emailed to validate); a cancelled ticket frees its MC seat
/// (waitlist promoted + notified). Gated by the `attendee-reconcile` feature +
/// `zoho.enabled`. Runs hourly.
/// </summary>
public sealed class AttendeeBackstageSyncJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _options;
    private readonly AttendeeTicketSyncService _sync;
    private readonly MasterClassEmailService _mcEmail;
    private readonly MasterClassPromotionEmailService _promo;
    private readonly AttendeeWelcomeProvisioningService _provisioning;
    private readonly WelcomeWithLoginEmailService _welcome;
    private readonly FeatureGateService _gate;
    private readonly IConfiguration _config;
    private readonly ILogger<AttendeeBackstageSyncJob> _log;

    public AttendeeBackstageSyncJob(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions options,
        AttendeeTicketSyncService sync, MasterClassEmailService mcEmail,
        MasterClassPromotionEmailService promo,
        AttendeeWelcomeProvisioningService provisioning,
        WelcomeWithLoginEmailService welcome, FeatureGateService gate,
        IConfiguration config, ILogger<AttendeeBackstageSyncJob> log)
    {
        _db = db; _zoho = zoho; _options = options; _sync = sync;
        _mcEmail = mcEmail; _promo = promo; _provisioning = provisioning;
        _welcome = welcome; _gate = gate; _config = config; _log = log;
    }

    [Function("AttendeeBackstageSyncJob")]
    public async Task Run([TimerTrigger("0 20 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (!_options.Enabled) { _log.LogInformation("AttendeeBackstageSyncJob: Zoho disabled."); return; }

        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("AttendeeBackstageSyncJob: no active event."); return; }
        if (!await _gate.IsFeatureEnabledAsync("attendee-reconcile", eventId.Value, ct))
        { _log.LogInformation("AttendeeBackstageSyncJob: feature off."); return; }

        var token = await _zoho.GetAccessTokenAsync(ct);
        if (token is null) { _log.LogError("AttendeeBackstageSyncJob: no Zoho token."); return; }

        var attendees = await _zoho.GetBackstageAttendeesAsync(token, ct);
        var rows = attendees.Select(AttendeeTicketSyncService.FromBackstage).ToList();
        var result = await _sync.SyncAsync(eventId.Value, rows, ct);

        var domain = _config["Hub:CustomDomain"];
        var baseUrl = string.IsNullOrWhiteSpace(domain) ? "https://eldk27.eventhub.expertslive.dk" : $"https://{domain}";

        var reEmails = 0;
        foreach (var r in result.Reassignments)
            try { if (await _mcEmail.SendReassignmentValidationAsync(r.AttendeeId, r.InheritedMcTitle, baseUrl, ct)) reEmails++; } catch { }
        var promoEmails = 0;
        foreach (var p in result.FreedPromotions)
            if (p.PromotedSignupId is int id)
                try { if (await _promo.SendPromotionAsync(id, baseUrl, ct)) promoEmails++; } catch { }

        _log.LogInformation(
            "AttendeeBackstageSyncJob: {Pulled} attendees — created {C}, updated {U}, reassigned {R} ({RE} validated), cancelled {X} ({PE} promoted).",
            rows.Count, result.Created, result.Updated, result.Reassigned, reEmails, result.Cancelled, promoEmails);

        // Attendee welcome auto-provisioning — DEFAULT OFF (mass email; organizer
        // turns it on deliberately). When on: create active login-capable Attendee
        // Participants for 2-day holders + email a one-click magic-link welcome to
        // the NEWLY-created ones only (idempotent — never re-emails).
        if (await _gate.IsFeatureEnabledAsync("attendee-welcome", eventId.Value, ct))
        {
            var newIds = await _provisioning.ProvisionAsync(eventId.Value, ct);
            var welcomed = 0;
            foreach (var pid in newIds)
                try { if ((await _welcome.SendForAttendeeProvisioningAsync(pid, baseUrl, ct)).Sent) welcomed++; }
                catch (Exception ex) { _log.LogWarning(ex, "AttendeeBackstageSyncJob: welcome send failed for participant {Pid}.", pid); }
            if (newIds.Count > 0)
                _log.LogInformation("AttendeeBackstageSyncJob: provisioned {N} attendee logins, welcomed {W}.", newIds.Count, welcomed);
        }
    }
}
