using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// Daily timer that pulls speakers from the Sessionize v2 view API and upserts
/// them for the active edition. Replaces the manual Excel upload as the
/// hands-off path: the same upsert semantics run (match on email, never
/// overwrite roles, report skipped rows) via <see cref="SessionizeApiImportService"/>.
///
/// No-op (logged) when the Sessionize integration is disabled or no event is
/// active. <c>sendWelcome:false</c> - a scheduled pull never emails speakers;
/// welcomes are sent manually from the participants page when the lineup is set.
///
/// DELTA mode (the default): adds NEW speakers and fills only empty,
/// never-speaker-edited bio fields. A speaker's own edits in the hub are NEVER
/// flushed by this scheduled sync — only the organizer "Full import" button
/// (<see cref="SessionizeImportMode.Full"/>) force-refreshes them.
/// </summary>
public sealed class SessionizeImportJob
{
    private readonly SessionizeApiImportService _service;
    private readonly SessionizeApiOptions _options;
    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly ILogger<SessionizeImportJob> _log;

    // ⚰️ §879 — `SpeakerApprovalService`, `EngineAlertSender` and `IConfiguration` were injected
    // ONLY to build and send the §304 pending-speaker mail, which SpeakersHeldJob now owns. Left
    // in place they would be a dependency nobody could explain, and the next reader would assume
    // this job still mails.
    public SessionizeImportJob(
        SessionizeApiImportService service,
        SessionizeApiOptions options,
        CommunityHubDbContext db,
        FeatureGateService gate,
        IAuditTrail audit,
        ILogger<SessionizeImportJob> log)
    {
        _service = service;
        _options = options;
        _db = db;
        _gate = gate;
        _audit = audit;
        _log = log;
    }

    /// <summary>
    /// A BASE TICK, not the cadence — the real frequency is the operator-editable
    /// <c>JobRunState.MinIntervalMinutes</c>, defaulting to <c>JobCatalog.DefaultIntervalMinutes</c>
    /// and enforced centrally by <c>JobsPauseMiddleware</c>.
    /// </summary>
    /// <remarks>
    /// §825 — that default is now <b>60</b> (operator 2026-08-04: <i>"the queue functionality and
    /// notification mail for pending speakers must run every 1 hr"</i>). This job both drains the
    /// CEH→Zoho hand-entry queue and sends the pending-speaker notice, and at ten minutes it produced
    /// five notices in half an hour all saying the same thing (measured 3 Aug: 21:06, 21:15, 21:20,
    /// 21:30, 21:30) — the pattern that trains someone to stop reading the Action queue.
    ///
    /// <para>🔒 <b>The cron stays a 5-minute tick on purpose.</b> Making it hourly TOO would stack an
    /// hourly tick on an hourly throttle, and a single missed or slightly-early tick then costs a
    /// whole hour instead of five minutes. The tick is cheap; the throttle is the cadence.</para>
    /// </remarks>
    [Function("SessionizeImportJob")]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation(
                "SessionizeImportJob: Sessionize API disabled by config.");
            return;
        }

        var activeEventId = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (activeEventId is null)
        {
            _log.LogWarning("SessionizeImportJob: no active event in DB.");
            return;
        }

        // GATE (REQUIREMENTS §23): the Sessionize import is an advanced feature,
        // off by default. When disabled for this edition the scheduled pull
        // no-ops — no speakers/sessions fetched or upserted. The organizer's
        // manual "Pull from Sessionize API" button honours the same gate.
        if (!await _gate.IsFeatureEnabledAsync("sessionize-import", activeEventId.Value, ct))
        {
            _log.LogInformation(
                "SessionizeImportJob: event {EventId} — feature 'sessionize-import' disabled, skipped.",
                activeEventId.Value);
            return;
        }

        var result = await _service.ImportAsync(
            activeEventId.Value, ct, sendWelcome: false,
            mode: SessionizeImportMode.Delta);

        if (result.Error is not null)
        {
            _log.LogWarning(
                "SessionizeImportJob: {Error}", result.Error);
            await RecordRunAsync(activeEventId.Value, AuditOutcome.Failure,
                $"Sessionize import failed: {result.Error}", ct);
            return;
        }

        _log.LogInformation(
            "SessionizeImportJob: speakers fetched {Fetched}, created {Created}, "
            + "updated {Updated}, skipped {Skipped}, warnings {Warnings}.",
            result.Fetched, result.Created, result.Updated, result.Skipped,
            result.Warnings.Count);

        // Only audit a run that actually CHANGED something — idle hourly pulls
        // (0 created/updated) would flood the trail with no signal.
        if (result.Created > 0 || result.Updated > 0)
            await RecordRunAsync(activeEventId.Value, AuditOutcome.Success,
                $"Sessionize import: {result.Fetched} fetched, {result.Created} created, "
                + $"{result.Updated} updated, {result.Skipped} skipped", ct);

        // ⚰️ §879 — THE §304 PENDING-SPEAKER MAIL NO LONGER GOES FROM HERE. `SpeakersHeldJob` owns
        // it, runs every 10 minutes, and mails the SAME body (the §877 approve-all buttons included
        // — it calls the same `SpeakerApprovalService.BuildPendingMailHtml`).
        //
        // 🔒 WHY IT HAD TO MOVE RATHER THAN COEXIST. That job speaks once per CHANGE, keyed on a
        // durable hash of the held set. A second sender here would have mailed on the import AND
        // again on the job's next pass, because the import does not (and should not) know the job's
        // fingerprint. §765 learned the same lesson from the other side: with two callers, the one
        // whose cadence the operator can SEE is not the one deciding.
        //
        // ⚠️ The cost is a delay of at most ten minutes on a brand-new import — which is exactly the
        // ten minutes he asked for (2026-08-05: *"i need that to run every 10 min and be notified
        // after 10 min"*), and in exchange a held speaker is now reported every ten minutes for as
        // long as they are held, not only in the pass that happened to create them.

        if (result.Sessions is { } sx)
        {
            if (sx.Error is not null)
            {
                _log.LogWarning("SessionizeImportJob: sessions: {Error}", sx.Error);
            }
            else
            {
                _log.LogInformation(
                    "SessionizeImportJob: sessions fetched {Fetched}, created {Created}, "
                    + "updated {Updated}, links +{LinksCreated}/-{LinksRemoved}.",
                    sx.Fetched, sx.Created, sx.Updated, sx.LinksCreated, sx.LinksRemoved);
            }
        }
    }

    // Named Engine event in the unified audit trail (REQUIREMENTS §24).
    private Task RecordRunAsync(int eventId, AuditOutcome outcome, string summary, CancellationToken ct) =>
        _audit.RecordAsync(new AuditEntry
        {
            EventId = eventId,
            Category = AuditCategory.Engine,
            Action = "sessionize-import",
            ActorEmail = "system",
            Source = AuditSource.Job,
            Summary = summary,
            Outcome = outcome,
        }, ct);
}
