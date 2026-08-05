using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// The CEH→Zoho push timer job. **Runs EVERY 5 MINUTES** (see the TimerTrigger below), asking the
/// session + speaker push engines to create the active edition's sessions and speakers in Zoho
/// Backstage. Zoho must be enabled and there must be an active edition. NEVER deletes; idempotent
/// (1:1 by the stored Backstage id). On any create/update failure the developer is alerted via the
/// ring-exempt EngineAlertSender.
/// <para>
/// 🔒 §569 — THERE ARE NO LONGER ANY STAGE/DIRECTION OR RING GATES on this path. A Sessionize
/// session flows to Zoho, and a categorized+active speaker syncs, full stop. Only the kill switches
/// (<c>Zoho:Enabled</c>, <c>backstage-speaker-sync</c> on/off, the external-write guard) and the
/// test-session exclusions still hold anything back.
/// </para>
/// </summary>
/// <remarks>
/// ⚠️ THE DOC COMMENT THAT USED TO BE HERE SAID "Hourly" AND ":05/:20/:35/:50" — BOTH WRONG, and
/// the operator read them while diagnosing a live outage on 2026-07-28 and concluded the job was
/// too infrequent ("i need this job to run more frequent"). The cron below has been every 5 minutes
/// throughout. Stale cadence comments cost real diagnosis time — if the cron changes, change this.
/// </remarks>
public sealed class SessionBackstagePushJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoOptions _options;
    private readonly SessionBackstagePushService _sessions;
    private readonly SpeakerBackstagePushService _speakers;
    private readonly EngineAlertSender _alerts;
    private readonly ILogger<SessionBackstagePushJob> _log;
    // §545(b) — optional, so a job can be instrumented without touching its wiring and an
    // un-instrumented job simply says nothing (silence = UNKNOWN, never flagged).
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public SessionBackstagePushJob(
        CommunityHubDbContext db, ZohoOptions options,
        SessionBackstagePushService sessions, SpeakerBackstagePushService speakers,
        EngineAlertSender alerts, ILogger<SessionBackstagePushJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db; _options = options; _sessions = sessions; _speakers = speakers;
        _alerts = alerts; _log = log; _activity = activity;
    }

    [Function("SessionBackstagePushJob")]
    // CADENCE: EVERY 5 MINUTES. §433 (operator 2026-07-27: "can you change the frequency of speaker
    // push to every 15 min") moved it off hourly-at-:50, and it was subsequently tightened again to
    // */5 — which is what actually runs. During live testing an hourly push meant up to an hour of
    // waiting to find out whether a completed Get Started had reached Zoho.
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("SessionBackstagePushJob: Zoho disabled.");
            _activity?.ReportInactive("Zoho is switched off, so no sessions or speakers are pushed.");
            return;
        }

        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SessionBackstagePushJob: no active event."); return; }

        // --- Speakers FIRST (§58 stage 2; operator 2026-07-24) ---
        // ORDER MATTERS: the speaker engine creates the RICH speaker record (name, tagline,
        // bio, links); a session create then LINKS the existing record by e-mail. Sessions
        // pushed first would make Zoho auto-create hollow speaker stubs from the bare
        // e-mails instead — and a session can never have speakers attached later (the
        // sessions API is create-only), so the speaker must exist before its session.
        var spr = await _speakers.RunAsync(eventId.Value, ct);
        if (!spr.DirectionActive)
        {
            _log.LogInformation("SessionBackstagePushJob (speakers): {Reason}.", spr.InactiveReason);
            // 🔒 §544 IS THIS LINE. "session sync direction is stage 1 … CEH→Zoho push inactive"
            // logged every run for WEEKS at Information level, invisible to him, until 1,500
            // attendees had no agenda. It is now a counted, alertable state.
            _activity?.ReportInactive(
                "Speaker push: " + (spr.InactiveReason ?? "the sync direction excludes it") + ".");
        }
        else if (!spr.SourceAvailable)
        {
            _log.LogWarning("SessionBackstagePushJob (speakers): source unavailable — {Reason}", spr.UnavailableReason);
            _activity?.ReportInactive(
                "Speaker push: source unavailable — " + (spr.UnavailableReason ?? "reason not given") + ".");
        }
        else
        {
            _activity?.ReportWork();
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
                    // §752.9 — DEV-silent: DEV pushes test data at Zoho and these failures are an
                    // expected property of that, not news. PROD still alerts, where a failed push
                    // means a real speaker is missing from Backstage.
                    ct, throttleKey: "SessionBackstagePushJob.speakers", devSilent: true);
            }
        }

        // --- Sessions SECOND (§57 stage 2) — after the speakers they reference exist. ---
        var sr = await _sessions.RunAsync(eventId.Value, ct);
        if (!sr.DirectionActive)
        {
            _log.LogInformation("SessionBackstagePushJob (sessions): {Reason}.", sr.InactiveReason);
            _activity?.ReportInactive(
                "Session push: " + (sr.InactiveReason ?? "the sync direction excludes it") + ".");
        }
        else if (!sr.SourceAvailable)
        {
            _log.LogWarning("SessionBackstagePushJob (sessions): source unavailable — {Reason}", sr.UnavailableReason);
            _activity?.ReportInactive(
                "Session push: source unavailable — " + (sr.UnavailableReason ?? "reason not given") + ".");
        }
        else
        {
            // Either half doing real work clears the streak — ReportWork() wins over ReportInactive().
            _activity?.ReportWork();
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
                    // §752.9 — DEV-silent, same reasoning as the speaker push above.
                    ct, throttleKey: "SessionBackstagePushJob.sessions", devSilent: true);
            }
        }
    }

    private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
