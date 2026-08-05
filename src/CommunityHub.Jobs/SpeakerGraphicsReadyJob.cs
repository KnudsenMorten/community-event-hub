using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// "Help Promote" notifier (§26c) — the DAILY SAFETY NET.
///
/// <para>The mail itself (and its idempotency, ring gate and audit) lives in
/// <see cref="SpeakerGraphicsReadyNotifier"/>, which is shared with the two LIVE triggers
/// added in §436: the organizer's Release click and the quarter-hourly SharePoint sync
/// (which pulls and auto-releases). Those two are what make the mail arrive when the
/// graphic becomes usable.</para>
///
/// <para>This sweep is kept because the live triggers can miss — a host restart mid-release,
/// a release made straight in the database, a transient mail failure (the ledger only
/// records a send that actually happened, so a failure retries here). It re-sends nothing:
/// the shared ledger key means an already-notified speaker is skipped.</para>
/// </summary>
public sealed class SpeakerGraphicsReadyJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SpeakerGraphicsReadyNotifier _notifier;
    private readonly ILogger<SpeakerGraphicsReadyJob> _log;

    public SpeakerGraphicsReadyJob(
        CommunityHubDbContext db,
        SpeakerGraphicsReadyNotifier notifier,
        ILogger<SpeakerGraphicsReadyJob> log)
    {
        _db = db;
        _notifier = notifier;
        _log = log;
    }

    /// <summary>
    /// §707.17 (operator 2026-07-30: *"run it every 30 min"*) — every 30 minutes; was daily 08:30 UTC.
    /// This is the CATCH-UP pass: a speaker is normally told the moment a graphic is RELEASED, so it
    /// only picks up whoever the live release missed. At a daily cadence a missed speaker waited up
    /// to 24 hours, which makes a safety net that is not much of one.
    ///
    /// <para>§869.3a — HE NAMED THIS JOB: *"same for this - i must be able to control timer so it
    /// runs every 10 min"*. His 30 minutes is now the §510 DEFAULT interval over a 5-minute base
    /// tick, so how long the safety net waits is set on the Jobs page, with no deploy.</para>
    /// </summary>
    [Function("SpeakerGraphicsReadyJob")]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogInformation("SpeakerGraphicsReadyJob: no active event."); return; }

        var sent = await _notifier.NotifyAsync(eventId.Value, onlySpeakerIds: null, ct);
        _log.LogInformation("SpeakerGraphicsReadyJob: {Sent} speaker(s) notified by the daily sweep.", sent);
    }
}
