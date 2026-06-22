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

    /// <summary>Hourly, at the top of every hour UTC (matches scheduledJobs.sessionizeImport cron).</summary>
    [Function("SessionizeImportJob")]
    public async Task Run(
        [TimerTrigger("0 0 * * * *")] TimerInfo timer,
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
