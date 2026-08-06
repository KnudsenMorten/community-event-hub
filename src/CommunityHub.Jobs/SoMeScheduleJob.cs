using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §824.21 — plans the SoMe announcement campaign: works out which posts are missing, when each
/// should go out, composes it from the edition's template, and queues it <b>held for approval</b>.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Nothing this job creates can publish on its own.</b> Every row is written
/// <c>IsActive = false</c>, and the dispatcher only publishes an ACTIVE queued post — so the campaign
/// can be planned continuously while nothing reaches the company page until he turns a row on
/// (§824.8 Q2: <i>"generate + schedule automatically, publish only after your approval"</i>).</para>
///
/// <para>Daily rather than hourly: the thing it reacts to is a new sponsor or a new session, which
/// arrive on the order of days. A tighter cadence would re-scan the whole edition to do nothing.</para>
/// </remarks>
public sealed class SoMeScheduleJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SoMeScheduleService _scheduler;
    private readonly SoMeAutoApproveService? _autoApprove;
    private readonly ILogger<SoMeScheduleJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public SoMeScheduleJob(
        CommunityHubDbContext db,
        SoMeScheduleService scheduler,
        ILogger<SoMeScheduleJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null,
        SoMeAutoApproveService? autoApprove = null)
    {
        _db = db;
        _scheduler = scheduler;
        _log = log;
        _activity = activity;
        _autoApprove = autoApprove;
    }

    [Function("SoMeScheduleJob")]
    // §878 — BASE TICK ONLY; the cadence is the operator's interval on /Organizer/Jobs.
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogInformation("SoMeScheduleJob: no active edition; skipped.");
            _activity?.ReportInactive(
                "There is no ACTIVE edition, so no SoMe announcements are being planned.");
            return;
        }

        _activity?.ReportWork();

        var result = await _scheduler.RunAsync(eventId.Value, ct);

        _log.LogInformation(
            "SoMeScheduleJob: {Created} post(s) queued (held for approval), {Already} already planned. {Message}",
            result.Created, result.AlreadyPlanned, result.Message);

        // §918 — AFTER planning, never before: a post has to exist before it can be approved, and
        // running it here means the same tick that creates a post can also approve it once it is
        // far enough out. 🔒 The service is a no-op unless he has switched it on.
        if (_autoApprove is not null)
        {
            var auto = await _autoApprove.RunAsync(eventId.Value, ct);
            if (auto.Approved > 0 || auto.Blocked > 0)
            {
                _log.LogInformation("§918 {Message}", auto.Message);
            }
        }

        if (result.Created == 0 && result.AlreadyPlanned == 0 && result.Message.Contains("footer"))
        {
            // §824.23 — the edition is not ready to be announced. At Warning because it will stay
            // true every day until someone fills the settings in, and a daily "did nothing" at
            // Information is indistinguishable from a healthy steady state.
            _log.LogWarning("SoMeScheduleJob: {Message}", result.Message);
            _activity?.ReportInactive(result.Message);
            return;
        }

        if (result.NoRoom.Count > 0)
        {
            // ⚠️ Named at Warning, never swallowed: "we ran out of weekdays before the event" is a
            // decision someone has to make (drop some, or post more per day), and an absence is
            // exactly what nobody notices.
            _log.LogWarning(
                "SoMeScheduleJob: {Count} announcement(s) could not fit before the event — {Items}.",
                result.NoRoom.Count, string.Join(", ", result.NoRoom.Take(20)));
        }
    }
}
