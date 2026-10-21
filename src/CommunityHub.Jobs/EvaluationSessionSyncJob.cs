using CommunityHub.Core.Data;
using CommunityHub.Core.Evaluation;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1268 — mirror CEH's sessions into Session Evaluation every 10 minutes, so a room or time change
/// reaches the kiosks and the attribution without anyone pressing "Sync sessions from CEH".
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-21: *"i propose we have a service that syncs every 10 min as we will forget
/// this"* · *"or job as we call it"*. The button on <c>/Organizer/SessionFeedback</c> stays — this runs
/// the SAME <see cref="EvaluationSessionSyncService.SyncAsync"/>, never a copy of it (the §1068 rule:
/// sync work flows through one routine).</para>
///
/// <para>🔒 <b>Why it is safe to run unattended.</b> The sync is idempotent (an unchanged session is
/// not touched), CEH is the master so it only ever copies in, and every change is named in its
/// result. What a change can set off downstream is already guarded: a session's report is re-sent
/// only when that session itself moved (§1264), and a TEST session mails only ring 0/1 (§1263).</para>
///
/// <para>🔑 Interval-driven (5-minute base tick, 10-minute default) so the cadence can be changed on
/// /Organizer/Jobs with no deploy. Nothing to gate: there is no feature switch whose OFF state would
/// make stale attribution correct.</para>
/// </remarks>
public sealed class EvaluationSessionSyncJob
{
    private readonly CommunityHubDbContext _db;
    private readonly EvaluationSessionSyncService _sync;
    private readonly ILogger<EvaluationSessionSyncJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public EvaluationSessionSyncJob(
        CommunityHubDbContext db,
        EvaluationSessionSyncService sync,
        ILogger<EvaluationSessionSyncJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db;
        _sync = sync;
        _log = log;
        _activity = activity;
    }

    [Function("EvaluationSessionSyncJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _activity?.ReportInactive("No active edition, so there are no sessions to sync.");
            return;
        }

        var result = await _sync.SyncAsync(eventId.Value, ct);

        var changed = result.RoomsCreated + result.SessionsCreated + result.SessionsUpdated
                      + result.Backfilled + result.SpeakerLinksAdded + result.SpeakerLinksRemoved;
        if (changed == 0)
        {
            // Nothing moved — the normal case, and not an error.
            _activity?.ReportInactive("Every session already matches CEH; nothing to change.");
            return;
        }

        _activity?.ReportWork();
        _log.LogInformation(
            "EvaluationSessionSyncJob: {Created} session(s) created, {Updated} updated, {Rooms} room(s) "
            + "created, {Backfilled} response(s) attributed, speaker links +{Added}/-{Removed}. {Changes}",
            result.SessionsCreated, result.SessionsUpdated, result.RoomsCreated, result.Backfilled,
            result.SpeakerLinksAdded, result.SpeakerLinksRemoved, string.Join(" | ", result.Changes));
        foreach (var w in result.Warnings)
            _log.LogWarning("EvaluationSessionSyncJob: {Warning}", w);
    }
}
