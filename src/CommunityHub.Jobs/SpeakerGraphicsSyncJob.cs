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
/// (<see cref="GraphicsService.PullSessionGraphicsAsync"/>) and then RELEASES anything still
/// unreleased (<see cref="GraphicsService.ReleaseAllGeneratedAsync"/>).
///
/// <para>🔒 §784.12(a): there is no review gate any more. Speaker-facing graphics are born
/// Released, so the bulk release here is the BACKLOG sweep — the rows that predate that decision —
/// and it no longer distinguishes pulled from engine-rendered artwork.</para>
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
    private readonly SoMeBundleBuildService _bundles;
    private readonly ILogger<SpeakerGraphicsSyncJob> _log;

    public SpeakerGraphicsSyncJob(
        GraphicsService graphics,
        CommunityHubDbContext db,
        IAuditTrail audit,
        SpeakerGraphicsReadyNotifier ready,
        SoMeBundleBuildService bundles,
        ILogger<SpeakerGraphicsSyncJob> log)
    {
        _graphics = graphics;
        _db = db;
        _audit = audit;
        _ready = ready;
        _bundles = bundles;
        _log = log;
    }

    /// <summary>
    /// §869.3 — every 15 minutes, as a §510 interval over a 5-minute base tick. This was the row in
    /// his screenshot: "0 10,25,40,55 * * * · set in code", with no input beside neighbours that had
    /// one.
    ///
    /// <para>⚠️ The four fixed slots USED to be justified by being offset from the Sessionize import
    /// at :00, so sessions existed before their graphics were matched by title. <b>That anchor is
    /// already gone</b> — §825 made the Sessionize import interval-driven (hourly over a base tick),
    /// so it no longer lands at :00 and the offset had nothing left to be offset from. The ordering
    /// argument had quietly stopped being true before this change; converting only makes that
    /// visible. Matching by title is idempotent, so a pass that runs early simply matches fewer.</para>
    ///
    /// <para>§435 (operator 2026-07-27): he dropped a file in the SharePoint MasterClass folder and
    /// expected it within *"1-5 min"*, then asked <i>"is the folder or name or method wrong"</i>.
    /// Neither the folder nor the name — this schedule. Hourly is right for a settled event and
    /// wrong for someone testing, so it now matches the §433 speaker-push cadence. The manual
    /// "Pull now" on /Organizer/Graphics remains the instant path.</para>
    /// </summary>
    [Function("SpeakerGraphicsSyncJob")]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
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

        // §767 PHASE 1 — BUILD the GIF bundles (per track, per sponsor tier) before anything else.
        // ⚠️ Deliberately AHEAD of the pull, because the pull RETURNS EARLY when the SharePoint
        // graphics store is not configured. Put the build after it and the sweep would silently
        // never run in exactly the setup where it matters most.
        // Inert and self-logging when there is no template; never throws the job.
        try
        {
            var bundles = await _bundles.BuildAsync(activeEventId.Value, ct);
            // 🔑 Only a run that actually RENDERED something is worth an audit row. A quiet sweep is
            // the steady state every 15 minutes; auditing it would bury the runs that matter.
            if (bundles.TrackBundles > 0 || bundles.SponsorBundles > 0
                || bundles.SessionGraphics > 0 || bundles.SponsorGraphics > 0)
            {
                await _audit.RecordAsync(new AuditEntry
                {
                    EventId = activeEventId.Value,
                    Category = AuditCategory.Engine,
                    Action = "some-graphics-bundles",
                    ActorEmail = "system",
                    Source = AuditSource.Job,
                    Summary = $"§767: {bundles.TrackBundles} track GIF(s), "
                              + $"{bundles.SessionGraphics} session graphic(s), "
                              + $"{bundles.SponsorBundles} sponsor grouping GIF(s), "
                              + $"{bundles.SponsorGraphics} sponsor graphic(s) built.",
                }, ct);
            }
        }
        catch (Exception ex)
        {
            // A failed render must not cost the PULL + RELEASE below — those are what speakers are
            // waiting on. Logged loudly rather than swallowed.
            _log.LogError(ex, "§767 bundle build failed; continuing with the SharePoint pull.");
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

        // RELEASE every generated speaker/session/track graphic to its speaker.
        //
        // 🔒 §784.12(a) RETIRED THE ENGINE-RENDERED CARVE-OUT (operator 2026-08-03: generated files
        // "should be published to speaker right away so they can see them"). This used to pass
        // includeEngineRendered:FALSE so machine-made artwork waited for an organizer click. It now
        // releases everything, and new rows are already born Released
        // (GraphicsService.InitialStatusFor) — so in the steady state this call finds nothing.
        // ⚠️ Its remaining job is the BACKLOG: the rows that were sitting at Generated when this
        // shipped. Without it the decision would only have applied to future graphics.
        var released = await _graphics.ReleaseAllGeneratedAsync(
            activeEventId.Value, "system (SharePoint sync)", ct, includeEngineRendered: true);

        // §436 (operator 2026-07-27: "when it detects, i expect also an email to arrive").
        // THIS is the detection path he meant: the file appears in SharePoint, this job pulls it,
        // and the speaker can act on it in the same run — so the mail goes out here, quarter-hourly,
        // instead of waiting for the next 08:30 sweep.
        //
        // 🔒 A FULL SWEEP (null), NOT released.SpeakerIds. Under §784.12(a) a pulled or rendered
        // graphic is ALREADY Released when it is written, so the bulk release above returns an empty
        // set in the steady state — and an empty (not null) set means "notify nobody". Passing it
        // would have silently reduced §436 back to the daily sweep on the very first run after the
        // backlog cleared. The narrowing was only ever an optimisation; the ReminderEngine ledger
        // key is the idempotency, and it still collapses re-runs about the same graphics to one mail.
        var notified = await _ready.NotifyAsync(activeEventId.Value, onlySpeakerIds: null, ct);

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
