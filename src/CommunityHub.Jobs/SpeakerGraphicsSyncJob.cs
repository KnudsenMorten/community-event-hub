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
    private readonly SpeakerGraphicsReadyNotifier _ready;
    private readonly ILogger<SpeakerGraphicsSyncJob> _log;

    public SpeakerGraphicsSyncJob(
        GraphicsService graphics,
        CommunityHubDbContext db,
        IAuditTrail audit,
        SpeakerGraphicsReadyNotifier ready,
        ILogger<SpeakerGraphicsSyncJob> log)
    {
        _graphics = graphics;
        _db = db;
        _audit = audit;
        _ready = ready;
        _log = log;
    }

    /// <summary>
    /// Every 15 minutes at :10/:25/:40/:55 UTC. The :40 slot is kept — it is offset from the
    /// Sessionize import at :00 so the sessions exist before their graphics are matched by title,
    /// and that reason still holds for the slot that matters most.
    ///
    /// <para>§435 (operator 2026-07-27): he dropped a file in the SharePoint MasterClass folder and
    /// expected it within *"1-5 min"*, then asked <i>"is the folder or name or method wrong"</i>.
    /// Neither the folder nor the name — this schedule. Hourly is right for a settled event and
    /// wrong for someone testing, so it now matches the §433 speaker-push cadence. The manual
    /// "Pull now" on /Organizer/Graphics remains the instant path.</para>
    /// </summary>
    [Function("SpeakerGraphicsSyncJob")]
    public async Task Run(
        [TimerTrigger("0 10,25,40,55 * * * *")] TimerInfo timer,
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

        // §436 (operator 2026-07-27: "when it detects, i expect also an email to arrive").
        // THIS is the detection path he meant: the file appears in SharePoint, this job pulls
        // it and — because placing the file IS the curation — releases it in the same run. So
        // the mail goes out here, quarter-hourly, instead of waiting for the next 08:30 sweep.
        // Narrowed to the speakers this run actually released; the shared ledger key means an
        // already-notified speaker is skipped either way.
        var notified = await _ready.NotifyAsync(activeEventId.Value, released.SpeakerIds, ct);

        _log.LogInformation(
            "SpeakerGraphicsSyncJob: pulled {Matched} session-matched ({Unmatched} had no file), "
            + "{TracksMatched} track(s) matched, released {Released} (notified {Notified} speaker(s)), "
            + "retired {Retired} (§326af — file deleted in SharePoint).",
            pull.Matched, pull.Unmatched, pull.TracksMatched, released.Count, notified, pull.Retired);

        if (pull.Matched > 0 || pull.TracksMatched > 0 || released.Count > 0 || pull.Retired > 0)
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = activeEventId.Value,
                Category = AuditCategory.Engine,
                Action = "speaker-graphics-sync",
                ActorEmail = "system",
                Source = AuditSource.Job,
                Summary = $"SharePoint graphics sync: {pull.Matched} session graphic(s) pulled, "
                          + $"{pull.TracksMatched} track(s) matched, {released.Count} released to speakers"
                          + (pull.Unmatched > 0 ? $", {pull.Unmatched} session(s) had no matching file" : ""),
                Outcome = AuditOutcome.Success,
            }, ct);
    }
}
