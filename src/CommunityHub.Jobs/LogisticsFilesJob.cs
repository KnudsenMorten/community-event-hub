using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §6.4 — rebuilds every §3.5 logistics file daily and mails on the schedule.
/// </summary>
/// <remarks>
/// <para>Files rebuild DAILY; the food and expo files mail WEEKLY; each hotel's rooming list mails
/// ON CHANGE, from three weeks before the event. All of that lives in
/// <see cref="LogisticsRunService"/> — this job is the clock and nothing else.</para>
///
/// <para>🔒 <b>Every mail goes to the operator's review mailbox until he approves the reports</b>
/// (§770.4). The recipients in §5.6 are external — a venue, a hotel — and an unreviewed spreadsheet
/// that reaches one of them cannot be recalled.</para>
///
/// <para>⚠️ <b>DEV writes nothing.</b> The §340-H external-write guard is inside the publisher's
/// store, so a DEV run reports itself inactive rather than pushing files into the live library.</para>
/// </remarks>
public sealed class LogisticsFilesJob
{
    private readonly CommunityHubDbContext _db;
    private readonly LogisticsRunService _run;
    private readonly ILogger<LogisticsFilesJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public LogisticsFilesJob(
        CommunityHubDbContext db,
        LogisticsRunService run,
        ILogger<LogisticsFilesJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db;
        _run = run;
        _log = log;
        _activity = activity;
    }

    // Base tick only — the real spacing is the operator's §510 interval on the Jobs page.
    // ⚠️ Read the SKIP LINE in the log, not this cron, to know how often it actually runs (§768.17).
    [Function("LogisticsFilesJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("LogisticsFilesJob: no active event.");
            return;
        }

        var result = await _run.RunAsync(eventId.Value, ct);

        if (!result.Ran)
        {
            _log.LogInformation("LogisticsFilesJob: {Reason}", result.InactiveReason);
            _activity?.ReportInactive(result.InactiveReason ?? "No logistics files were produced.");
            return;
        }

        _activity?.ReportWork();

        _log.LogInformation(
            "LogisticsFilesJob: {Published} published, {Unchanged} unchanged, {Mailed} mailed.",
            result.Published, result.Unchanged, result.Mailed);

        // 🔑 Skips and failures are logged as WARNINGS, individually and by name. A run that
        // silently published nine of eleven files reads as a success in the count alone — and the
        // two missing ones are a venue order nobody placed.
        foreach (var s in result.Skipped)
            _log.LogWarning("LogisticsFilesJob skipped: {Detail}", s);

        foreach (var f in result.Failures)
            _log.LogWarning("LogisticsFilesJob failed: {Detail}", f);

        // 🔒 §774 — the people who need a room and are in no hotel's list, BY NAME.
        // ⚠️ A count alone would be useless here: "3 awaiting a hotel" cannot be acted on, and the
        // whole point of this line is that somebody goes and places those three. It is a WARNING
        // rather than information because a successful run is exactly what hides it — every file
        // the run was asked for was produced, and these people were in none of them.
        if (result.Unplaced.Count > 0)
        {
            _log.LogWarning(
                "LogisticsFilesJob: {Count} person(s) need a room and are placed in no hotel — {Names}",
                result.Unplaced.Count, string.Join(", ", result.Unplaced));
        }
    }
}
