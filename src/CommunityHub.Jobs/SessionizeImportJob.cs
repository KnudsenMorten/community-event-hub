using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
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
    private readonly ILogger<SessionizeImportJob> _log;

    public SessionizeImportJob(
        SessionizeApiImportService service,
        SessionizeApiOptions options,
        CommunityHubDbContext db,
        ILogger<SessionizeImportJob> log)
    {
        _service = service;
        _options = options;
        _db = db;
        _log = log;
    }

    /// <summary>Daily at 02:00 UTC (matches scheduledJobs.sessionizeImport cron).</summary>
    [Function("SessionizeImportJob")]
    public async Task Run(
        [TimerTrigger("0 0 2 * * *")] TimerInfo timer,
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

        var result = await _service.ImportAsync(
            activeEventId.Value, ct, sendWelcome: false,
            mode: SessionizeImportMode.Delta);

        if (result.Error is not null)
        {
            _log.LogWarning(
                "SessionizeImportJob: {Error}", result.Error);
            return;
        }

        _log.LogInformation(
            "SessionizeImportJob: speakers fetched {Fetched}, created {Created}, "
            + "updated {Updated}, skipped {Skipped}, warnings {Warnings}.",
            result.Fetched, result.Created, result.Updated, result.Skipped,
            result.Warnings.Count);

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
}
