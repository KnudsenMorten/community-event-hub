using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// Hourly timer that SYNCS the speaker/session SoMe graphics from SharePoint to the hub
/// (REQUIREMENTS §18/§158). The operator pre-stages one finished graphic per session in the
/// configured SharePoint folders (file name = session title); this job PULLS them
/// (<see cref="GraphicsService.PullSessionGraphicsAsync"/>) and then RELEASES every pulled
/// graphic to its speaker (<see cref="GraphicsService.ReleaseAllGeneratedAsync"/>). Because the
/// operator's act of placing the file in the folder IS the curation, the pulled graphic is
/// released straight to the speaker — no separate per-graphic review click.
///
/// INERT (logged, no-op) when the graphics SharePoint store is not configured (no site / folder
/// paths) or no event is active. Idempotent: a re-run upserts the same rows by stable key and
/// re-releases nothing already released.
/// </summary>
public sealed class SpeakerGraphicsSyncJob
{
    private readonly GraphicsService _graphics;
    private readonly CommunityHubDbContext _db;
    private readonly IAuditTrail _audit;
    private readonly ILogger<SpeakerGraphicsSyncJob> _log;

    public SpeakerGraphicsSyncJob(
        GraphicsService graphics,
        CommunityHubDbContext db,
        IAuditTrail audit,
        ILogger<SpeakerGraphicsSyncJob> log)
    {
        _graphics = graphics;
        _db = db;
        _audit = audit;
        _log = log;
    }

    /// <summary>Hourly, at :40 past the hour UTC (offset from the Sessionize import at :00 so the
    /// sessions exist before their graphics are matched by title).</summary>
    [Function("SpeakerGraphicsSyncJob")]
    public async Task Run(
        [TimerTrigger("0 40 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var activeEventId = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (activeEventId is null)
        {
            _log.LogWarning("SpeakerGraphicsSyncJob: no active event in DB.");
            return;
        }

        // PULL from SharePoint (inert/zero when not configured — never throws). Pulls the
        // title-matched session graphics AND (§158) the Track-matched per-track graphics in the
        // SAME run, so both release on the same cadence.
        var pull = await _graphics.PullSessionGraphicsAsync(activeEventId.Value, ct);
        if (pull.Matched == 0 && pull.Unmatched == 0 && pull.TracksMatched == 0)
        {
            _log.LogInformation(
                "SpeakerGraphicsSyncJob: SharePoint graphics store not configured (or no sessions) — nothing pulled.");
            return;
        }

        // RELEASE every generated (pulled) speaker/session/track graphic to its speaker.
        var released = await _graphics.ReleaseAllGeneratedAsync(
            activeEventId.Value, "system (SharePoint sync)", ct);

        _log.LogInformation(
            "SpeakerGraphicsSyncJob: pulled {Matched} session-matched ({Unmatched} had no file), "
            + "{TracksMatched} track(s) matched, released {Released}.",
            pull.Matched, pull.Unmatched, pull.TracksMatched, released);

        if (pull.Matched > 0 || pull.TracksMatched > 0 || released > 0)
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = activeEventId.Value,
                Category = AuditCategory.Engine,
                Action = "speaker-graphics-sync",
                ActorEmail = "system",
                Source = AuditSource.Job,
                Summary = $"SharePoint graphics sync: {pull.Matched} session graphic(s) pulled, "
                          + $"{pull.TracksMatched} track(s) matched, {released} released to speakers"
                          + (pull.Unmatched > 0 ? $", {pull.Unmatched} session(s) had no matching file" : ""),
                Outcome = AuditOutcome.Success,
            }, ct);
    }
}
