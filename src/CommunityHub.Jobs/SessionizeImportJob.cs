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
    private readonly CommunityHub.Core.Organizer.SpeakerApprovalService _approval;
    private readonly CommunityHub.Core.Email.EngineAlertSender _alerts;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    private readonly ILogger<SessionizeImportJob> _log;

    public SessionizeImportJob(
        SessionizeApiImportService service,
        SessionizeApiOptions options,
        CommunityHubDbContext db,
        FeatureGateService gate,
        IAuditTrail audit,
        CommunityHub.Core.Organizer.SpeakerApprovalService approval,
        CommunityHub.Core.Email.EngineAlertSender alerts,
        Microsoft.Extensions.Configuration.IConfiguration config,
        ILogger<SessionizeImportJob> log)
    {
        _service = service;
        _options = options;
        _db = db;
        _gate = gate;
        _audit = audit;
        _approval = approval;
        _alerts = alerts;
        _config = config;
        _log = log;
    }

    /// <summary>Hourly, at the top of every hour UTC (matches scheduledJobs.sessionizeImport cron).</summary>
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

        // §304 (operator 2026-07-24): NEW speakers arrive Ring 3 / inactive /
        // uncategorized (fail-closed) and are HELD from the Zoho flow — mail info@
        // IMMEDIATELY with the pending list + the admin link so the organizer can set
        // category + ring (Save activates) and the speaker "flows to zoho fast".
        // Fires only when this pass CREATED someone (never on idle hourly pulls).
        if (result.Created > 0)
        {
            try
            {
                var pending = await _approval.PendingAsync(activeEventId.Value, ct);
                var domain = _config["Hub:CustomDomain"];
                var baseUrl = string.IsNullOrWhiteSpace(domain)
                    ? "https://eldk27.eventhub.expertslive.dk" : $"https://{domain}";
                var html = CommunityHub.Core.Organizer.SpeakerApprovalService
                    .BuildPendingMailHtml(pending, baseUrl);
                if (html is not null)
                {
                    await _alerts.AlertAsync(
                        $"ACTION: {pending.Speakers.Count} pending speaker(s) need approval for the Zoho flow [ELDK27]",
                        html, ct, throttleKey: null,
                        recipient: CommunityHub.Core.Email.ZohoChangeNotifier.Recipient);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SessionizeImportJob: pending-speaker mail failed.");
            }
        }

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
