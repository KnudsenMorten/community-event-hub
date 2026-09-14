using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1060(b) — mails each speaker / sponsor about the post ELDK has scheduled about them: once when
/// it is scheduled, once the day before it publishes.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Both sends are offered on EVERY pass and the ledger decides.</b> That is deliberate:
/// the job holds no state and no "which stage is this post on" bookkeeping, so a missed run, a
/// restart or a re-approval cannot skip a stage or repeat one. The occasion key is post + stage, so
/// the second mail is a genuinely new occasion rather than a repeat of the first (§664 — a key
/// without the occasion in it means "told once, ever").</para>
///
/// <para>⚠️ <b>A post approved less than 24 h before its slot gets BOTH mails in the same pass</b>,
/// which is correct: the person is being told it is scheduled and that it is imminent, and those are
/// both true. It is not a duplicate — the bodies differ and the ledger keys differ.</para>
///
/// <para>Cadence: BASE TICK ONLY (§878); the real interval is the operator's setting on
/// <c>/Organizer/Jobs</c>. Cheap to run often — a pass with nothing newly due sends nothing.</para>
/// </remarks>
public sealed class SoMeAnnouncementNoticeJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SoMeAnnouncementNotifier _notifier;
    private readonly ILogger<SoMeAnnouncementNoticeJob> _log;

    public SoMeAnnouncementNoticeJob(
        CommunityHubDbContext db, SoMeAnnouncementNotifier notifier,
        ILogger<SoMeAnnouncementNoticeJob> log)
    {
        _db = db; _notifier = notifier; _log = log;
    }

    [Function("SoMeAnnouncementNoticeJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null) { _log.LogWarning("SoMeAnnouncementNoticeJob: no active event."); return; }

        var r = await _notifier.RunAsync(eventId.Value, ct);
        _log.LogInformation("SoMeAnnouncementNoticeJob: {Result}.", r);
    }
}
