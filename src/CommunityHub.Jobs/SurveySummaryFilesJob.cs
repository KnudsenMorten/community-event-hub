using CommunityHub.Core.Data;
using CommunityHub.Core.Surveys;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §6.5 — rebuilds the three post-event survey summaries daily and writes them to their §3.4 folders.
/// </summary>
/// <remarks>
/// <para>🔒 <b>It never mails.</b> §6.5: <i>"No notification mail for these three."</i> The
/// summaries live in the document library and are read there.</para>
///
/// <para>⚠️ <b>DEV writes nothing</b> — the §340-H external-write guard lives inside the publisher's
/// store, so a DEV run reports itself inactive rather than pushing files into the live library.</para>
/// </remarks>
public sealed class SurveySummaryFilesJob
{
    private readonly CommunityHubDbContext _db;
    private readonly SurveySummaryPublishService _run;
    private readonly ILogger<SurveySummaryFilesJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public SurveySummaryFilesJob(
        CommunityHubDbContext db,
        SurveySummaryPublishService run,
        ILogger<SurveySummaryFilesJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db;
        _run = run;
        _log = log;
        _activity = activity;
    }

    // Base tick only — the real spacing is the operator's §510 interval on the Jobs page.
    [Function("SurveySummaryFilesJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("SurveySummaryFilesJob: no active event.");
            return;
        }

        var result = await _run.RunAsync(eventId.Value, ct);

        if (!result.Ran)
        {
            _log.LogInformation("SurveySummaryFilesJob: {Reason}", result.InactiveReason);
            _activity?.ReportInactive(result.InactiveReason ?? "No survey summaries were produced.");
            return;
        }

        _activity?.ReportWork();

        _log.LogInformation(
            "SurveySummaryFilesJob: {Published} published, {Unchanged} unchanged.",
            result.Published, result.Unchanged);

        // 🔑 By name, and as warnings. A summary that quietly stopped being written looks exactly
        // like a summary nobody has responded to.
        foreach (var s in result.Skipped)
            _log.LogWarning("SurveySummaryFilesJob skipped: {Detail}", s);

        foreach (var f in result.Failures)
            _log.LogWarning("SurveySummaryFilesJob failed: {Detail}", f);
    }
}
