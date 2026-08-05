using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §784.7(C) — ONE row per session answering everything about it: its QR code, whether it can
/// collect feedback, its score, and whether its report has been released.
/// </summary>
/// <param name="SessionId">
/// The <b>CEH</b> session id when there is one — the id the QR code and the report are keyed on, and
/// the one an organizer means by "the session". Null only for an evaluation-side session that has
/// never been matched to a CEH session (see <paramref name="EvaluationSessionId"/>).
/// </param>
/// <param name="EvaluationSessionId">
/// The EVALUATION model's session id, which is what responses and scores are keyed on. Null when CEH
/// has a session the evaluation system has not mirrored yet — that session cannot collect anything.
/// </param>
/// <param name="Title">The session title, preferring CEH's (it is the master).</param>
/// <param name="Room">Room name, from the evaluation mirror (it is what the devices are placed in).</param>
/// <param name="ScheduledStart">When it runs; null when only CEH knows about it and has no mirror.</param>
/// <param name="HasQrCode">True when a permanent public token exists, i.e. a code can be printed.</param>
/// <param name="Score">Its satisfaction figures, or null when nothing is mirrored to score.</param>
/// <param name="Responses">How many raw responses are attributed to it.</param>
/// <param name="SpeakerNames">Who presents it — the reason an organizer is usually on this page.</param>
/// <param name="HasScorePdf">A published SCORE report exists in the store (§783.9 "released").</param>
/// <param name="HasOpenPdf">A published OPEN-FEEDBACK report exists in the store.</param>
public sealed record SessionFeedbackRow(
    int? SessionId,
    int? EvaluationSessionId,
    string Title,
    string? Room,
    DateTimeOffset? ScheduledStart,
    bool HasQrCode,
    SatisfactionScore.Result? Score,
    int Responses,
    IReadOnlyList<string> SpeakerNames,
    bool HasScorePdf,
    bool HasOpenPdf)
{
    /// <summary>
    /// §783.9 — the report state the merged page shows: <c>released</c> once the engine has
    /// published either PDF for this session.
    /// </summary>
    /// <remarks>
    /// ⚠️ This is NOT the same question as §784.12(a)'s graphics auto-release, and the two must not
    /// be collapsed: a graphic being visible to its speaker and an evaluation REPORT having been
    /// published are different events with different rules.
    /// </remarks>
    public bool ReportReleased => HasScorePdf || HasOpenPdf;

    /// <summary>
    /// Can this session gather feedback at all? It needs a mirrored evaluation session (the devices
    /// and the scan both resolve through it) AND a room for a device to be placed in.
    /// </summary>
    /// <remarks>
    /// ⚠️ This is the question <c>/Organizer/EvaluationSessions</c> existed to answer, and it is the
    /// one that is worth knowing BEFORE the day: a session nobody mirrored collects nothing, and the
    /// QR code prints perfectly and then says "not open yet" when scanned.
    /// </remarks>
    public bool CanCollect => EvaluationSessionId is not null && Room is not null;

    /// <summary>True when the session exists in CEH but the evaluation system has not mirrored it.</summary>
    public bool NotMirrored => EvaluationSessionId is null;

    /// <summary>True when the evaluation side holds a session CEH does not know about.</summary>
    public bool OrphanedFromCeh => SessionId is null;
}

/// <summary>
/// §784.7(C) — assembles the merged per-session feedback view that replaced four organizer pages
/// (Evaluation sessions, Evaluation results, Session QR codes and the report-state list).
/// </summary>
/// <remarks>
/// <para>🔒 <b>THE JOIN IS THE WHOLE POINT, AND IT IS NOT OBVIOUS.</b> The four merged pages were
/// keyed on TWO different session ids:</para>
/// <list type="bullet">
///   <item><b>QR codes and evaluation reports</b> are keyed on the <b>CEH</b> <c>Session.Id</c>;</item>
///   <item><b>responses and scores</b> are keyed on the <b>evaluation</b> <c>EvaluationSession.Id</c>.</item>
/// </list>
/// <para>They meet only through <c>EvaluationSession.CehSessionId</c>, which is <b>nullable on both
/// sides in practice</b>. Merging them by position, title or order would produce a page that shows a
/// score against the wrong session — a failure nobody would spot, because every row would still look
/// plausible.</para>
///
/// <para>⚠️ <b>Neither leftover is dropped.</b> A CEH session with no mirror is listed and marked as
/// unable to collect (its QR prints but says "not open yet" when scanned); an evaluation session with
/// no CEH match is listed as orphaned rather than silently vanishing with its responses. Losing
/// either would make the merged page quieter — and less true — than the pages it replaced.</para>
///
/// <para>Read-only. Every write (mint a token, sync the mirror, run the publish pass) stays on its
/// own action, as §784.7 requires: they are different privileges at different moments.</para>
/// </remarks>
public sealed class SessionFeedbackOverviewService
{
    private readonly CommunityHubDbContext _db;
    private readonly EvaluationScoreService _scores;
    private readonly SessionEvalPdfService _pdf;

    public SessionFeedbackOverviewService(
        CommunityHubDbContext db, EvaluationScoreService scores, SessionEvalPdfService pdf)
    {
        _db = db;
        _scores = scores;
        _pdf = pdf;
    }

    /// <summary>Every session in the edition, earliest first, with all four facts resolved.</summary>
    public async Task<IReadOnlyList<SessionFeedbackRow>> BuildAsync(
        int eventId, CancellationToken ct = default)
    {
        // CEH's sessions — the master list, and what carries the QR token.
        //
        // ⚠️ SERVICE SESSIONS ARE EXCLUDED (lunch, breaks, registration). Nobody rates a coffee
        // break, and `SessionEvalPdfService.ListSessionsAsync` — the report side of this same page —
        // has always excluded them. The retired QR page did NOT, so it listed them as sessions
        // missing a code. Including them here would fill the merged page with rows reading
        // "not collecting · no report", which is exactly the noise this merge exists to remove.
        var cehSessions = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .Select(s => new { s.Id, s.Title, s.PublicToken })
            .ToListAsync(ct);

        // The evaluation mirror — what responses and scores are keyed on.
        var evalSessions = await _db.EvaluationSessions
            .Where(s => s.EventId == eventId)
            .Select(s => new
            {
                s.Id, s.CehSessionId, s.Title, s.ScheduledStart,
                Room = s.Room != null ? s.Room.Name : null,
            })
            .ToListAsync(ct);

        var scoreById = (await _scores.ListAsync(eventId, ct: ct))
            .ToDictionary(s => s.SessionId);

        var responsesById = await _db.EvaluationResponses
            .Where(r => r.EventId == eventId && r.SessionId != null)
            .GroupBy(r => r.SessionId!.Value)
            .Select(g => new { SessionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SessionId, x => x.Count, ct);

        // Report state (§783.9): a row has a released report when the store holds its PDF. 🔒 Asked
        // of the STORE, not of a provenance table — §783.2 fixed a speaker-facing button that a
        // stale provenance row rendered for a file that could only ever 404. This also carries the
        // speaker names, which is the fact that made the old report list worth opening.
        var pdfRows = (await _pdf.ListSessionsAsync(eventId, ct))
            .GroupBy(r => r.SessionId)
            .ToDictionary(g => g.Key, g => g.First());

        var evalByCehId = evalSessions
            .Where(e => e.CehSessionId is not null)
            .GroupBy(e => e.CehSessionId!.Value)
            // A duplicate mirror for one CEH session is a data fault, not a reason to throw on a
            // read-only page: keep the first and let the row render.
            .ToDictionary(g => g.Key, g => g.First());

        var rows = new List<SessionFeedbackRow>(cehSessions.Count + evalSessions.Count);

        foreach (var s in cehSessions)
        {
            evalByCehId.TryGetValue(s.Id, out var ev);
            var evalId = ev?.Id;
            pdfRows.TryGetValue(s.Id, out var pdf);

            rows.Add(new SessionFeedbackRow(
                SessionId: s.Id,
                EvaluationSessionId: evalId,
                Title: s.Title,
                Room: ev?.Room ?? pdf?.Room,
                ScheduledStart: ev?.ScheduledStart,
                HasQrCode: !string.IsNullOrWhiteSpace(s.PublicToken),
                Score: evalId is int id && scoreById.TryGetValue(id, out var sc) ? sc.Score : null,
                Responses: evalId is int rid && responsesById.TryGetValue(rid, out var n) ? n : 0,
                SpeakerNames: pdf?.SpeakerNames ?? Array.Empty<string>(),
                HasScorePdf: pdf?.Score is not null,
                HasOpenPdf: pdf?.Open is not null));
        }

        // The other leftover: evaluation sessions never matched to a CEH session. They hold real
        // responses, so hiding them would hide feedback.
        var matched = evalByCehId.Values.Select(e => e.Id).ToHashSet();
        foreach (var ev in evalSessions.Where(e => !matched.Contains(e.Id)))
        {
            rows.Add(new SessionFeedbackRow(
                SessionId: null,
                EvaluationSessionId: ev.Id,
                Title: ev.Title,
                Room: ev.Room,
                ScheduledStart: ev.ScheduledStart,
                HasQrCode: false,
                Score: scoreById.TryGetValue(ev.Id, out var sc) ? sc.Score : null,
                Responses: responsesById.TryGetValue(ev.Id, out var n) ? n : 0,
                SpeakerNames: Array.Empty<string>(),
                HasScorePdf: false,
                HasOpenPdf: false));
        }

        // Scheduled first (earliest to latest), then the ones with no schedule, by title — so the
        // page reads like the day rather than like a database.
        return rows
            .OrderBy(r => r.ScheduledStart is null)
            .ThenBy(r => r.ScheduledStart)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
