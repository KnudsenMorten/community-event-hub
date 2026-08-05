using CommunityHub.Core.Data;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §750 C7 — decide WHEN a session's report is rebuilt, published and notified. The brief:
/// <i>"Regeneration is debounced — wait until 30 minutes have passed with no new responses for that
/// session before rebuilding, so a trickle of late cached data does not produce a stream of
/// superseding reports and notification emails."</i>
/// </summary>
/// <remarks>
/// <para>🔑 <b>Figures update live; DOCUMENTS settle.</b> That split is the whole design. Scores,
/// distribution and counts are derived on every read and are already current the moment a response
/// lands — nothing here gates them. What this gates is the expensive, noisy half: rendering a PDF and
/// emailing people about it.</para>
///
/// <para>🔒 <b>Why a debounce and not a timer after the window closes.</b> A device that was offline
/// flushes its cache hours or days later (§743 C4 — late data is normal operation, not an error). A
/// "publish 30 minutes after the window closes" rule would publish once and then be wrong forever,
/// while a per-response rule would emit a superseding report and an email PER RECORD of that flush.
/// The quiet period is the only signal that a session has actually stopped moving.</para>
///
/// <para>🔒 <b>The version, not a dirty flag.</b> Whether anything changed is decided by comparing the
/// version derived from current data against
/// <see cref="Domain.Evaluation.EvaluationSession.PublishedReportVersion"/>. A flag would have to be
/// set by every write path that can affect a report — responses, but also a C3 retitle or room move —
/// and the one that gets forgotten produces a stale PDF that nobody knows is stale.</para>
/// </remarks>
public sealed class EvaluationReportDebounceService
{
    private readonly CommunityHubDbContext _db;
    private readonly EvaluationReportBuilder _builder;
    private readonly EvaluationArtifactPublishService _publisher;
    private readonly EvaluationReportReadyMailService _mail;
    private readonly TimeProvider _clock;
    private readonly ILogger<EvaluationReportDebounceService> _log;

    public EvaluationReportDebounceService(
        CommunityHubDbContext db,
        EvaluationReportBuilder builder,
        EvaluationArtifactPublishService publisher,
        EvaluationReportReadyMailService mail,
        ILogger<EvaluationReportDebounceService> log,
        TimeProvider? clock = null)
    {
        _db = db;
        _builder = builder;
        _publisher = publisher;
        _mail = mail;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// 🔒 The brief's number, as ONE constant. §743's grace period is a different 30 minutes (the
    /// collection tail) and they must be free to move independently — a shared constant would couple
    /// "how long we wait to publish" to "how late a press still counts", which are unrelated
    /// questions that merely happen to share a value today.
    /// </summary>
    public const int QuietMinutes = 30;

    /// <summary>Why a session was not published this pass. Diagnostics for the organiser view.</summary>
    public enum Outcome
    {
        /// <summary>Published and notified.</summary>
        Published,

        /// <summary>Nothing has changed since the last published version.</summary>
        AlreadyCurrent,

        /// <summary>Responses are still arriving — the quiet period has not elapsed.</summary>
        StillSettling,

        /// <summary>No report can be produced yet (no responses, or below the sample threshold).</summary>
        NoReportYet,
    }

    public sealed record SessionOutcome(int EvaluationSessionId, string Title, Outcome Outcome);

    public sealed record Result(
        int Published, int Superseded, IReadOnlyList<SessionOutcome> Sessions);

    /// <summary>
    /// One pass over an event: publish + notify every session that has changed and gone quiet.
    /// </summary>
    public async Task<Result> RunAsync(int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var quietBefore = now.AddMinutes(-QuietMinutes);

        var sessions = await _db.EvaluationSessions
            .Where(s => s.EventId == eventId && s.CehSessionId != null)
            .Select(s => new
            {
                s.Id, s.Title, s.CehSessionId, s.PublishedReportVersion,
            })
            .ToListAsync(ct);

        // The newest response per session, in ONE query rather than per session.
        // 🔑 ReceivedTimestamp, NOT CollectionTimestamp: the debounce is about when data stopped
        // ARRIVING. A device flushing week-old presses has a stale collection time and a brand new
        // received time, and it is the arrival that means "still moving".
        var lastReceived = await _db.EvaluationResponses
            .Where(r => r.EventId == eventId && r.SessionId != null)
            .GroupBy(r => r.SessionId!.Value)
            .Select(g => new { SessionId = g.Key, Last = g.Max(r => r.ReceivedTimestamp) })
            .ToDictionaryAsync(x => x.SessionId, x => x.Last, ct);

        var outcomes = new List<SessionOutcome>();
        int published = 0, superseded = 0;

        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();

            // 🔒 A session NOBODY rated is not published and nobody is mailed about it.
            //
            // The renderer would happily produce a valid PDF here — §743.14's "below the threshold
            // the report explains itself instead of printing a number" covers the empty case too, so
            // this is not a technical limit. It is a judgement about the mail: telling a speaker
            // "your evaluation results are ready" when the report contains nothing is worse than
            // sending nothing at all, and it is the same misreading §748.1 removed an empty ratings
            // grid to avoid — silence reads as "not collected yet", an empty report reads as
            // "nobody liked you enough to press a button".
            //
            // 🔑 Below-threshold-but-NONZERO still publishes: that report says something true and
            // useful ("too few responses to score"), and a speaker who got 4 presses is better served
            // by seeing them than by silence. The line is at zero, not at the threshold.
            if (!lastReceived.ContainsKey(s.Id))
            {
                outcomes.Add(new(s.Id, s.Title, Outcome.NoReportYet));
                continue;
            }

            var current = await _builder.VersionAsync(eventId, s.Id, ct);
            if (current is null)
            {
                outcomes.Add(new(s.Id, s.Title, Outcome.NoReportYet));
                continue;
            }

            if (string.Equals(current, s.PublishedReportVersion, StringComparison.Ordinal))
            {
                outcomes.Add(new(s.Id, s.Title, Outcome.AlreadyCurrent));
                continue;
            }

            if (lastReceived.TryGetValue(s.Id, out var last) && last > quietBefore)
            {
                outcomes.Add(new(s.Id, s.Title, Outcome.StillSettling));
                continue;
            }

            var isSupersede = !string.IsNullOrEmpty(s.PublishedReportVersion);

            try
            {
                if (!await _publisher.PublishReportAsync(eventId, s.CehSessionId!.Value, ct))
                {
                    outcomes.Add(new(s.Id, s.Title, Outcome.NoReportYet));
                    continue;
                }

                await _mail.NotifyAsync(eventId, s.Id, isSupersede, ct);

                // 🔒 Stamped LAST, and only once both halves succeeded. Stamping earlier would
                // convert a transient mail failure into a notification nobody ever receives, with
                // the row claiming it was sent.
                var row = await _db.EvaluationSessions.FirstAsync(x => x.Id == s.Id, ct);
                row.PublishedReportVersion = current;
                row.PublishedReportAt = now;
                await _db.SaveChangesAsync(ct);

                published++;
                if (isSupersede) superseded++;
                outcomes.Add(new(s.Id, s.Title, Outcome.Published));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One session's failure must not abandon the rest of the event. The version is NOT
                // stamped, so the next pass retries this session from scratch.
                _log.LogError(ex,
                    "Publishing/notifying the report for evaluation session {SessionId} failed.", s.Id);
                outcomes.Add(new(s.Id, s.Title, Outcome.NoReportYet));
            }
        }

        return new Result(published, superseded, outcomes);
    }
}
