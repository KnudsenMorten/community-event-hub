using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Evaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// §752 C10 — the SPEAKER's view of their own session results, on the four-point model.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this had to exist now.</b> §750 shipped a notification that says <i>"your
/// evaluation results are ready"</i> and links into the hub, while §748.5 had hidden
/// <c>/Speaker/Evaluations</c> (it read the retired 1–5 scale). The mail therefore landed a speaker on
/// a hub with nowhere to read the thing it had just promised them — the notification was built before
/// the page it points at.</para>
///
/// <para>🔒 <b>Own sessions only, enforced server-side.</b> The brief: <i>"a speaker must not be able
/// to read another session's page"</i>. Scope comes from the §749 speaker links resolved against the
/// signed-in participant's PRIMARY e-mail — the same key the report-ready mail is addressed by, so a
/// speaker who receives a mail can always open the thing it points at.</para>
///
/// <para>🔑 <b>Every figure is derived at request time</b> from the raw responses (§8), exactly as the
/// organiser view and the PDF are. There is no stored score to go stale, so late data simply shows up
/// the next time the page is opened.</para>
/// </remarks>
[Authorize]
public class ResultsModel : PageModel
{
    private static readonly ParticipantRole[] EligibleRoles = { ParticipantRole.Speaker };

    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly EvaluationScoreService _scores;
    private readonly EvaluationReportBuilder _builder;
    private readonly EvaluationReportService _reports;

    public ResultsModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db,
        EvaluationScoreService scores,
        EvaluationReportBuilder builder,
        EvaluationReportService reports)
    {
        _participant = participant;
        _db = db;
        _scores = scores;
        _builder = builder;
        _reports = reports;
    }

    public bool AccessDenied { get; private set; }
    public int Threshold => SatisfactionScore.MinimumResponses;

    /// <summary>One of the speaker's sessions, with its derived figures and its comments.</summary>
    public sealed record Row(
        int EvaluationSessionId,
        int? CehSessionId,
        string Title,
        string? Room,
        DateTimeOffset Start,
        DateTimeOffset End,
        SatisfactionScore.Result Score,
        IReadOnlyList<string> Comments,
        int QrCount,
        int DeviceCount);

    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!EligibleRoles.Contains(me.Role)) { AccessDenied = true; return Page(); }

        Rows = await LoadAsync(me.EventId, me.Email, ct);
        return Page();
    }

    /// <summary>
    /// §752 — download the same PDF the engine publishes. 🔒 Own-scope re-checked here, not trusted
    /// from the page that rendered the link: a handler is directly addressable.
    /// </summary>
    public async Task<IActionResult> OnGetReportAsync(int sessionId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!EligibleRoles.Contains(me.Role)) return NotFound();

        var mine = await MySessionIdsAsync(me.EventId, me.Email, ct);
        if (!mine.Contains(sessionId)) return NotFound();

        // 🔑 The SHARED builder + renderer — the speaker downloads the same document the organiser
        // sees and the engine publishes to SharePoint, never a second rendering that could drift.
        var report = await _builder.BuildAsync(me.EventId, sessionId, ct);
        if (report is null) return NotFound();

        var pdf = _reports.Render(report.Data);
        var safe = string.Join("_", report.Data.SessionTitle.Split(Path.GetInvalidFileNameChars()));
        return File(pdf, "application/pdf", $"evaluation-{safe}.pdf");
    }

    /// <summary>
    /// The speaker's own evaluation sessions, by PRIMARY e-mail (§743.2 / §749).
    /// </summary>
    private async Task<List<int>> MySessionIdsAsync(int eventId, string? email, CancellationToken ct)
    {
        var key = Core.Auth.PinLoginService.NormalizeEmail(email ?? string.Empty);
        if (key.Length == 0) return new List<int>();

        return await _db.EvaluationSessionSpeakers
            .Where(s => s.SpeakerEmail == key)
            .Join(_db.EvaluationSessions.Where(x => x.EventId == eventId),
                  s => s.EvaluationSessionId, x => x.Id, (s, x) => x.Id)
            .Distinct()
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<Row>> LoadAsync(int eventId, string? email, CancellationToken ct)
    {
        var ids = await MySessionIdsAsync(eventId, email, ct);
        if (ids.Count == 0) return Array.Empty<Row>();

        var sessions = await _db.EvaluationSessions
            .Where(s => ids.Contains(s.Id))
            .OrderBy(s => s.ScheduledStart)
            .ToListAsync(ct);

        var rows = new List<Row>();
        foreach (var s in sessions)
        {
            var score = await _scores.ForSessionAsync(eventId, s.Id, ct: ct);

            // 🔒 Free text is visible to the session's OWN speakers and organisers only (brief,
            // access control). Reaching it through the own-scope id list above is what enforces that.
            var comments = await _db.EvaluationResponses
                .Where(r => r.SessionId == s.Id && r.FreeText != null && r.FreeText != "")
                .OrderBy(r => r.CollectionTimestamp)
                .Select(r => r.FreeText!)
                .ToListAsync(ct);

            var qr = await _db.EvaluationResponses.CountAsync(
                r => r.SessionId == s.Id
                     && r.Source == Core.Domain.Evaluation.EvaluationResponseSources.Qr, ct);
            var device = await _db.EvaluationResponses.CountAsync(
                r => r.SessionId == s.Id
                     && r.Source != Core.Domain.Evaluation.EvaluationResponseSources.Qr, ct);

            var room = s.RoomId is null
                ? null
                : await _db.EvaluationRooms.Where(x => x.Id == s.RoomId)
                    .Select(x => x.Name).FirstOrDefaultAsync(ct);

            rows.Add(new Row(
                s.Id, s.CehSessionId, s.Title, room, s.ScheduledStart, s.ScheduledEnd,
                score?.Score ?? SatisfactionScore.Compute(new SatisfactionScore.Distribution(0, 0, 0, 0)),
                comments, qr, device));
        }

        return rows;
    }
}
