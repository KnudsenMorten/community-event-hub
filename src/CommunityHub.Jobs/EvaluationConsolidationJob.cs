using CommunityHub.Core.Data;
using CommunityHub.Core.Evaluation;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §6.6 — POST-EVENT: copies every session-evaluation result into the event folder, rebuilds the
/// combined all-session summary, and notifies the organizers when something moved.
/// </summary>
/// <remarks>
/// <para>🔒 <b>It reports itself INACTIVE until the event has ended</b>, with the end date in the
/// message. Silence before the event would be indistinguishable from a broken job, and this one is
/// deliberately idle for most of its life.</para>
///
/// <para>⚠️ <b>DEV writes nothing</b> — the §340-H external-write guard lives inside the publisher's
/// store, so a DEV run reports itself inactive rather than pushing files into the live library.</para>
/// </remarks>
public sealed class EvaluationConsolidationJob
{
    private readonly CommunityHubDbContext _db;
    private readonly EvaluationConsolidationService _run;
    private readonly ILogger<EvaluationConsolidationJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public EvaluationConsolidationJob(
        CommunityHubDbContext db,
        EvaluationConsolidationService run,
        ILogger<EvaluationConsolidationJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db;
        _run = run;
        _log = log;
        _activity = activity;
    }

    // Base tick only — the real spacing is the operator's §510 interval on the Jobs page.
    [Function("EvaluationConsolidationJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("EvaluationConsolidationJob: no active event.");
            return;
        }

        var result = await _run.RunAsync(eventId.Value, ct);

        if (!result.Ran)
        {
            _log.LogInformation("EvaluationConsolidationJob: {Reason}", result.InactiveReason);
            _activity?.ReportInactive(result.InactiveReason ?? "Nothing was consolidated.");
            return;
        }

        _activity?.ReportWork();

        _log.LogInformation(
            "EvaluationConsolidationJob: {Copied} copied, {Unchanged} unchanged, summary {Summary}, "
            + "notified {Notified}.",
            result.Copied, result.Unchanged,
            result.SummaryWritten ? "rewritten" : "unchanged", result.Notified);

        // 🔑 By name. A result PDF that silently failed to copy is a gap in the event record that
        // nothing else will ever point at.
        foreach (var f in result.Failures)
            _log.LogWarning("EvaluationConsolidationJob failed: {Detail}", f);
    }
}
