using System.IO.Compression;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §784.7(B)+(C) — <b>Session feedback</b>: the one page that answers "how is this session doing?".
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-03: *"it is confusing now"* … *"yes do the three-way merge"*. Five facts
/// about ONE session used to live on four pages — Evaluation sessions (is it mirrored, has it a
/// room), Session QR codes (can it be printed), Evaluation results (its score), and the report list
/// (has its report been released). Nothing was wrong with any of them individually; <b>the split was
/// the defect</b>, because answering one question meant visiting four pages.</para>
///
/// <para>🔒 <b>The WRITE actions are deliberately NOT merged into one control</b> (§784.7 build
/// constraint). Minting QR tokens, syncing the mirror from CEH and running the publish/notify pass
/// are different privileges at different moments; each keeps its own button, and the two that have
/// outward effects stay POST-only so a link, a prefetch or a refresh cannot trigger them.</para>
///
/// <para>🔒 <b>Mobile-first (~360px): one EXPANDING row per session, never a five-column grid.</b>
/// Five columns do not fit at 360px, and the CEH rule is that every UI change works there.</para>
///
/// <para>⚠️ It does NOT reintroduce the retired manual upload (§783.9): report state is
/// <c>released</c> / <c>not released</c>, and the engine is the only writer.</para>
///
/// <para>§784.7(B): this lives in the SPEAKER area (<c>/Organizer/Content</c>), not in Setup — it is
/// about the sessions and their speakers, not about configuring the platform. The device pages stay
/// in Setup, where commissioning belongs.</para>
/// </remarks>
[Authorize]
public class SessionFeedbackModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly SessionFeedbackOverviewService _overview;
    private readonly EvaluationScoreService _scores;
    private readonly EvaluationSessionSyncService _sync;
    private readonly EvaluationQrService _qr;
    private readonly SessionQrCodeService _codes;
    private readonly EvaluationReportBuilder _builder;
    private readonly EvaluationReportService _reports;
    private readonly EvaluationReportDebounceService _debounce;
    private readonly SessionEvalPdfService _pdfStore;
    private readonly EmailTemplateOptions _email;

    public SessionFeedbackModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db,
        SessionFeedbackOverviewService overview,
        EvaluationScoreService scores,
        EvaluationSessionSyncService sync,
        EvaluationQrService qr,
        SessionQrCodeService codes,
        EvaluationReportBuilder builder,
        EvaluationReportService reports,
        EvaluationReportDebounceService debounce,
        SessionEvalPdfService pdfStore,
        IOptions<EmailTemplateOptions> email)
    {
        _participant = participant;
        _db = db;
        _overview = overview;
        _scores = scores;
        _sync = sync;
        _qr = qr;
        _codes = codes;
        _builder = builder;
        _reports = reports;
        _debounce = debounce;
        _pdfStore = pdfStore;
        _email = email.Value;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public IReadOnlyList<string> Changes { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

    public IReadOnlyList<SessionFeedbackRow> Rows { get; private set; } = Array.Empty<SessionFeedbackRow>();

    // --- the event roll-up (was the top of Evaluation results) ---------------
    public SatisfactionScore.Result? Pooled { get; private set; }
    public double? MeanSession { get; private set; }
    public int Unattributed { get; private set; }
    public int Threshold => SatisfactionScore.MinimumResponses;
    public int QuietMinutes => EvaluationReportDebounceService.QuietMinutes;

    /// <summary>True when there is somewhere to publish reports to; named on the page when not.</summary>
    public bool PdfFolderConfigured { get; private set; }

    /// <summary>
    /// §750.3 — what the last publish pass decided, so an organizer can see WHY a report has not
    /// gone out. 🔑 Populated ONLY by "Run publish now": loading a diagnostics page must never
    /// publish or e-mail as a side effect of being looked at.
    /// </summary>
    public EvaluationReportDebounceService.Result? PublishResult { get; private set; }

    public string BaseUrl { get; private set; } = string.Empty;
    public bool BaseUrlMissing => string.IsNullOrWhiteSpace(BaseUrl);

    public int WithQrCode => Rows.Count(r => r.HasQrCode);
    public int MissingQrCode => Rows.Count(r => r.SessionId is not null && !r.HasQrCode);

    /// <summary>
    /// 🔒 The PUBLIC host, from the same setting e-mailed links use — NOT <c>Request.Host</c>. A code
    /// generated while someone was on a staging hostname would encode that hostname into printed
    /// material, and nothing downstream would catch it.
    /// </summary>
    private string PublicBaseUrl => (_email.HubUrl ?? string.Empty).Trim().TrimEnd('/');

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Re-sync the evaluation mirror from CEH. CEH is master; nothing here is edited.</summary>
    public async Task<IActionResult> OnPostSyncAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var result = await _sync.SyncAsync(me.EventId, ct);
        Changes = result.Changes;
        Warnings = result.Warnings;
        Message =
            $"Synced from CEH: {result.SessionsCreated} session(s) created, "
            + $"{result.SessionsUpdated} updated, {result.RoomsCreated} room(s) created"
            + (result.Skipped > 0 ? $", {result.Skipped} skipped" : "")
            + (result.Backfilled > 0
                ? $". {result.Backfilled} earlier response(s) were attributed retrospectively."
                : ".");

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Mint a QR token for every session that lacks one. 🔑 Bulk, because doing it session by session
    /// before a print run is exactly the pre-event task that gets abandoned half-done — and half-done
    /// means a session with no sign.
    /// </summary>
    public async Task<IActionResult> OnPostGenerateCodesAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        // ⚠️ Service sessions (lunch, breaks) are excluded, matching the listing — otherwise this
        // reports "generated 3 codes" for rows the page does not show, and the count never settles.
        var ids = await _db.Sessions
            .Where(s => s.EventId == me.EventId
                        && !s.IsServiceSession
                        && (s.PublicToken == null || s.PublicToken == ""))
            .Select(s => s.Id)
            .ToListAsync(ct);

        foreach (var id in ids) await _qr.EnsureTokenAsync(id, ct);

        Message = ids.Count == 0
            ? "Every session already has a code."
            : $"Generated codes for {ids.Count} session(s).";

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §750.3 — run the publish/notify pass NOW rather than waiting for the timer.
    /// 🔒 POST, not GET: it publishes files and sends e-mail. 🔑 It runs the SAME service the timer
    /// runs, quiet period included — a "force" that skipped the debounce would mean the thing you
    /// tested is not the thing that runs.
    /// </summary>
    public async Task<IActionResult> OnPostRunPublishAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        PublishResult = await _debounce.RunAsync(me.EventId, ct);
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>One session's QR PNG, for preview and print.</summary>
    public async Task<IActionResult> OnGetQrAsync(int sessionId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var session = await _db.Sessions
            .Where(s => s.Id == sessionId && s.EventId == me.EventId)
            .Select(s => new { s.Id, s.Title, s.PublicToken })
            .FirstOrDefaultAsync(ct);

        if (session is null || string.IsNullOrWhiteSpace(session.PublicToken)) return NotFound();
        if (string.IsNullOrWhiteSpace(PublicBaseUrl)) return BadRequest("Hub URL is not configured.");

        var png = _codes.RenderPng(PublicBaseUrl, session.PublicToken);
        return File(png, "image/png", SessionQrCodeService.FileName(session.Id, session.Title, "png"));
    }

    /// <summary>Every code in one ZIP — one request instead of 40 right-clicks before the print run.</summary>
    public async Task<IActionResult> OnGetDownloadAllQrAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        if (string.IsNullOrWhiteSpace(PublicBaseUrl)) return BadRequest("Hub URL is not configured.");

        var sessions = await _db.Sessions
            .Where(s => s.EventId == me.EventId && s.PublicToken != null && s.PublicToken != "")
            .OrderBy(s => s.Title)
            .Select(s => new { s.Id, s.Title, s.PublicToken })
            .ToListAsync(ct);

        if (sessions.Count == 0) return NotFound();

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var s in sessions)
            {
                var entry = zip.CreateEntry(
                    SessionQrCodeService.FileName(s.Id, s.Title, "png"), CompressionLevel.Optimal);
                using var stream = entry.Open();
                var png = _codes.RenderPng(PublicBaseUrl, s.PublicToken!);
                stream.Write(png, 0, png.Length);
            }
        }

        return File(buffer.ToArray(), "application/zip", "session-qr-codes.zip");
    }

    /// <summary>
    /// One session's PDF report, generated on request from the raw responses.
    /// 🔒 Generated, never stored: there is no cached file to go stale, and it is assembled by the
    /// SHARED builder so the outbound pull (§747 C8) hands external systems the same document.
    /// </summary>
    public async Task<IActionResult> OnGetReportAsync(int evaluationSessionId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var report = await _builder.BuildAsync(me.EventId, evaluationSessionId, ct);
        if (report is null) return NotFound();

        var pdf = _reports.Render(report.Data);
        var safeTitle = string.Join("_", report.Data.SessionTitle.Split(Path.GetInvalidFileNameChars()));
        return File(pdf, "application/pdf", $"evaluation-{evaluationSessionId}-{safeTitle}.pdf");
    }

    public string FeedbackUrlFor(string token) =>
        SessionQrCodeService.FeedbackUrl(PublicBaseUrl, token);

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        BaseUrl = PublicBaseUrl;
        Rows = await _overview.BuildAsync(eventId, ct);

        var (pooled, mean, unattributed) = await _scores.ForEventAsync(eventId, ct: ct);
        Pooled = pooled;
        MeanSession = mean;
        Unattributed = unattributed;
        PdfFolderConfigured = _pdfStore.CanManage;
    }
}
