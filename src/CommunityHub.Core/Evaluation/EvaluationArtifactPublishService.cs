using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §749.1/§749.2 — push the evaluation artefacts we GENERATE into SharePoint: the per-session QR
/// codes (C5) and the per-session report PDFs (C7).
/// </summary>
/// <remarks>
/// <para>🔑 <b>Operator 2026-07-31:</b> <i>"dont forget to download pdf files intonsharepoint source
/// with correct naming. solution exist in cost with paths"</i> and <i>"both qr code link download but
/// it must be changed as current is linked to room name but now we use sessionname"</i>. Both halves
/// are honoured here, and both reuse the EXISTING stores and path settings rather than introducing a
/// second SharePoint integration — which is precisely what he was telling us.</para>
///
/// <para>🔒 <b>The naming is not ours to invent.</b> PDFs use
/// <see cref="SessionEvalPdfService.FileNameFor"/> (<c>{CehSessionId}-scores.pdf</c>), whose own doc
/// comment already called itself <i>"the naming contract the external evaluation system will use when
/// it pushes PDFs into the DocLibrary folder itself"</i> — §299 OPEN-30 anticipated exactly this.
/// QR codes use <see cref="SessionQrCodeService.FileName"/> (<c>session-{id}-{slug}-qr.png</c>), which
/// is already session-keyed.</para>
///
/// <para>🔒 <b>INERT until configured</b>, like every other store user here: with no wired store or no
/// folder set, publishing reports "not configured" and writes nothing. Nothing is faked, nothing
/// throws — an unconfigured environment must not fail a job.</para>
///
/// <para>⚠️ <b>Uploading a report REPLACES the same-named file.</b> That is the intent of a
/// deterministic name — a regenerated report supersedes its predecessor rather than accumulating
/// <c>-v2</c> copies — but it also means an organiser's hand-uploaded <c>scores</c> PDF for a session
/// is overwritten once the engine publishes one. §299 OPEN-30 chose this contract knowingly.</para>
/// </remarks>
public sealed class EvaluationArtifactPublishService
{
    private readonly CommunityHubDbContext _db;
    private readonly SessionQrCodeService _qrCodes;
    private readonly SessionEvalsQrService _qrStore;
    private readonly SessionEvalPdfService _pdfStore;
    private readonly EvaluationReportBuilder _reportBuilder;
    private readonly EvaluationReportService _reportRenderer;
    private readonly ILogger<EvaluationArtifactPublishService> _log;

    public EvaluationArtifactPublishService(
        CommunityHubDbContext db,
        SessionQrCodeService qrCodes,
        SessionEvalsQrService qrStore,
        SessionEvalPdfService pdfStore,
        EvaluationReportBuilder reportBuilder,
        EvaluationReportService reportRenderer,
        ILogger<EvaluationArtifactPublishService> log)
    {
        _db = db;
        _qrCodes = qrCodes;
        _qrStore = qrStore;
        _pdfStore = pdfStore;
        // 🔑 §747's "one report, one assembly site": the builder assembles the data and the renderer
        // draws it, and BOTH are the same ones the organiser download and the C8 pull use. A second
        // assembly path here would be a second report definition, free to drift from the one people
        // actually receive.
        _reportBuilder = reportBuilder;
        _reportRenderer = reportRenderer;
        _log = log;
    }

    /// <summary>What one publish pass did. <paramref name="Notes"/> is the human-readable audit.</summary>
    public sealed record Result(
        int Published, int Skipped, bool Configured, IReadOnlyList<string> Notes);

    private static Result NotConfigured(string what) =>
        new(0, 0, false, new[] { $"{what}: SharePoint folder not configured — nothing written." });

    // ===================================================================
    //  QR codes (C5) — §749.2
    // ===================================================================

    /// <summary>
    /// Generate every session's QR PNG and store it in the QR folder, named by SESSION.
    /// </summary>
    /// <remarks>
    /// 🔑 Only sessions that already carry a <c>PublicToken</c> are published. Minting a token here
    /// would be a side effect of a "publish" call, and a token is the thing that gets PRINTED — it is
    /// created deliberately on the organiser page, not as a by-product of a file sync.
    /// </remarks>
    public async Task<Result> PublishQrCodesAsync(
        int eventId, string publicBaseUrl, CancellationToken ct = default)
    {
        if (!_qrStore.CanManage) return NotConfigured("QR codes");

        var sessions = await _db.Sessions
            .Where(s => s.EventId == eventId
                        && !s.IsServiceSession
                        && s.PublicToken != null && s.PublicToken != "")
            .Select(s => new { s.Id, s.Title, s.PublicToken })
            .ToListAsync(ct);

        var notes = new List<string>();
        int published = 0, skipped = 0;

        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var png = _qrCodes.RenderPng(publicBaseUrl, s.PublicToken!);
                var name = SessionQrCodeService.FileName(s.Id, s.Title, "png");

                await _qrStore.UploadAsync(name, png, "image/png", ct);
                published++;
                notes.Add($"QR published: {name}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 🔒 One bad session must not abandon the other 24. The failure is NAMED, because a
                // silently missing QR is discovered on the morning of the event.
                skipped++;
                notes.Add($"QR FAILED for session {s.Id} '{s.Title}': {ex.Message}");
                _log.LogError(ex, "Publishing the QR code for session {SessionId} failed.", s.Id);
            }
        }

        return new Result(published, skipped, true, notes);
    }

    // ===================================================================
    //  Report PDFs (C7) — §749.1
    // ===================================================================

    /// <summary>
    /// Build one session's report PDF and store it under the §299 naming contract, upserting the
    /// provenance row that the organiser and speaker views read.
    /// </summary>
    /// <param name="cehSessionId">
    /// 🔒 The CEH session id, NOT the <c>EvaluationSession</c> id. The file name and the
    /// <c>SessionEvaluationFile</c> row are both keyed on CEH's id — mixing the two would file a
    /// report under a different session's number, which reads as correct until someone opens it.
    /// </param>
    public async Task<bool> PublishReportAsync(
        int eventId, int cehSessionId, CancellationToken ct = default)
    {
        if (!_pdfStore.CanManage)
        {
            _log.LogInformation(
                "Report publish skipped for session {SessionId}: PDF folder not configured.",
                cehSessionId);
            return false;
        }

        var evalSession = await _db.EvaluationSessions
            .Where(s => s.EventId == eventId && s.CehSessionId == cehSessionId)
            .Select(s => new { s.Id })
            .FirstOrDefaultAsync(ct);
        if (evalSession is null) return false;

        var report = await _reportBuilder.BuildAsync(eventId, evalSession.Id, ct);
        if (report is null) return false;

        var pdf = _reportRenderer.Render(report.Data);
        if (pdf.Length == 0) return false;

        await _pdfStore.UploadAsync(
            eventId, cehSessionId, EvaluationPdfKind.Score, pdf,
            uploadedByParticipantId: null,
            // 🔑 Named so the provenance line reads honestly. The organiser view says
            // "uploaded by {name}" — "Organizer" would be a lie about who produced this file.
            uploadedByName: "Session Evaluation (automatic)",
            ct);

        _log.LogInformation(
            "Published the evaluation report for CEH session {SessionId} ({Bytes} bytes).",
            cehSessionId, pdf.Length);
        return true;
    }

    /// <summary>
    /// Publish the report for every mirrored session in the event that has one.
    /// </summary>
    public async Task<Result> PublishAllReportsAsync(int eventId, CancellationToken ct = default)
    {
        if (!_pdfStore.CanManage) return NotConfigured("Report PDFs");

        var sessions = await _db.EvaluationSessions
            .Where(s => s.EventId == eventId && s.CehSessionId != null)
            .Select(s => new { CehId = s.CehSessionId!.Value, s.Title })
            .ToListAsync(ct);

        var notes = new List<string>();
        int published = 0, skipped = 0;

        foreach (var s in sessions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await PublishReportAsync(eventId, s.CehId, ct))
                {
                    published++;
                    notes.Add($"Report published: {SessionEvalPdfService.FileNameFor(s.CehId, EvaluationPdfKind.Score)}");
                }
                else
                {
                    skipped++;
                    notes.Add($"No report yet for '{s.Title}' — below threshold or no responses.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped++;
                notes.Add($"Report FAILED for '{s.Title}': {ex.Message}");
                _log.LogError(ex, "Publishing the report for CEH session {SessionId} failed.", s.CehId);
            }
        }

        return new Result(published, skipped, true, notes);
    }
}
