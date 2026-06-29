using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// The §57/§58 STAGE 2 (CehToZoho) push timer job. Hourly, it asks the session + speaker
/// push engines to create/update the active edition's sessions in Zoho Backstage and create
/// its speakers — gated per-edition on the session/speaker sync direction being stage 2.
/// At the default stage 1 (Sessionize→CEH) and at stage 3 (Zoho→CEH, the §38e read engine)
/// each engine returns Inactive and this job no-ops. Zoho must be enabled and there must be
/// an active edition. NEVER deletes; idempotent (1:1 by the stored Backstage id). On any
/// create/update failure the developer is alerted via the ring-exempt EngineAlertSender.
/// </summary>
public sealed class SessionBackstagePushJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoOptions _options;
    private readonly SessionBackstagePushService _sessions;
    private readonly SpeakerBackstagePushService _speakers;
    private readonly EngineAlertSender _alerts;
    private readonly ILogger<SessionBackstagePushJob> _log;

    public SessionBackstagePushJob(
        CommunityHubDbContext db, ZohoOptions options,
        SessionBackstagePushService sessions, SpeakerBackstagePushService speakers,
        EngineAlertSender alerts, ILogger<SessionBackstagePushJob> log)
    {
        _db = db; _options = options; _sessions = sessions; _speakers = speakers;
        _alerts = alerts; _log = log;
    }

    [Function("SessionBackstagePushJob")]
    public async Task Run([TimerTrigger("0 50 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (!_options.Enabled) { _log.LogInformation("SessionBackstagePushJob: Zoho disabled."); return; }

        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SessionBackstagePushJob: no active event."); return; }

        // --- Sessions (§57 stage 2) ---
        var sr = await _sessions.RunAsync(eventId.Value, ct);
        if (!sr.DirectionActive)
        {
            _log.LogInformation("SessionBackstagePushJob (sessions): {Reason}.", sr.InactiveReason);
        }
        else if (!sr.SourceAvailable)
        {
            _log.LogWarning("SessionBackstagePushJob (sessions): source unavailable — {Reason}", sr.UnavailableReason);
        }
        else
        {
            _log.LogInformation(
                "SessionBackstagePushJob (sessions): created {Created}, updated {Updated}, failed {Failed}, skipped {Skipped}.",
                sr.Created, sr.Updated, sr.Failed, sr.Skipped);
            if (sr.Failed > 0)
            {
                var lines = string.Join("", sr.Items
                    .Where(i => i.Action == SessionBackstagePushService.PushAction.Failed)
                    .Select(i => $"<li>{Enc(i.Title)} — {Enc(i.Error)}</li>"));
                await _alerts.AlertAsync(
                    "Stage-2 CEH→Zoho session push: failures [ELDK27]",
                    $"<p>{sr.Failed} session push(es) failed:</p><ul>{lines}</ul>",
                    ct, throttleKey: "SessionBackstagePushJob.sessions");
            }
        }

        // --- Speakers (§58 stage 2) ---
        var spr = await _speakers.RunAsync(eventId.Value, ct);
        if (!spr.DirectionActive)
        {
            _log.LogInformation("SessionBackstagePushJob (speakers): {Reason}.", spr.InactiveReason);
        }
        else if (!spr.SourceAvailable)
        {
            _log.LogWarning("SessionBackstagePushJob (speakers): source unavailable — {Reason}", spr.UnavailableReason);
        }
        else
        {
            _log.LogInformation(
                "SessionBackstagePushJob (speakers): created {Created}, alreadyLinked {Linked}, failed {Failed}, skipped {Skipped}.",
                spr.Created, spr.AlreadyLinked, spr.Failed, spr.Skipped);
            if (spr.Failed > 0)
            {
                var lines = string.Join("", spr.Items
                    .Where(i => i.Action == SpeakerBackstagePushService.PushAction.Failed)
                    .Select(i => $"<li>{Enc(i.Email)} — {Enc(i.Error)}</li>"));
                await _alerts.AlertAsync(
                    "Stage-2 CEH→Zoho speaker push: failures [ELDK27]",
                    $"<p>{spr.Failed} speaker push(es) failed:</p><ul>{lines}</ul>",
                    ct, throttleKey: "SessionBackstagePushJob.speakers");
            }
        }
    }

    private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
