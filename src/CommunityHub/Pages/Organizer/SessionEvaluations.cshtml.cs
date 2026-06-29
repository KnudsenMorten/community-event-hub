using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ORGANIZER results dashboard for the per-session attendee EVALUATIONS (HappyOrNot-style
/// ratings collected from the public evaluate page / room QR). Shows per-session and
/// per-room aggregates — average score, count, comments — filterable by session type and
/// room. Read-only; the aggregation lives in <see cref="SessionEvaluationService"/> so the
/// dashboard never re-implements the math. Mobile-first (~360px) + a11y.
///
/// §166 — below the ratings dashboard, a "Final evaluation PDFs" section lists every
/// non-service session with a per-session UPLOAD form: the organizer uploads the final
/// evaluation PDF, it is stored on SharePoint (<see cref="SessionEvalPdfService"/>),
/// <see cref="Session.EvaluationFormUrl"/> is set to the HUB PROXY url (never a SharePoint
/// link), and the session's speaker(s) are emailed that their evaluation is ready.
/// </summary>
[Authorize]
public class SessionEvaluationsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SessionEvaluationService _svc;
    private readonly SessionEvalPdfService _pdf;
    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _email;
    private readonly ILogger<SessionEvaluationsModel> _log;

    public SessionEvaluationsModel(
        ICurrentParticipantAccessor participant,
        SessionEvaluationService svc,
        SessionEvalPdfService pdf,
        CommunityHubDbContext db,
        IEmailSender email,
        ILogger<SessionEvaluationsModel> log)
    {
        _participant = participant;
        _svc = svc;
        _pdf = pdf;
        _db = db;
        _email = email;
        _log = log;
    }

    public bool AccessDenied { get; private set; }

    // --- Filters (querystring) ---------------------------------------------
    [BindProperty(SupportsGet = true)] public SessionType? FilterType { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterRoom { get; set; }

    // --- View state --------------------------------------------------------
    public SessionEvaluationService.DashboardResult? Dashboard { get; private set; }
    public List<string> Rooms { get; private set; } = new();

    // --- §166 final-evaluation-PDF section ---------------------------------
    /// <summary>True when the SharePoint folder for final eval PDFs is wired for uploads.</summary>
    public bool PdfConfigured { get; private set; }
    /// <summary>Every non-service session, with its speaker names + PDF-uploaded status.</summary>
    public IReadOnlyList<SessionEvalPdfRow> PdfSessions { get; private set; } = Array.Empty<SessionEvalPdfRow>();
    public string? PdfMessage { get; private set; }
    public string? PdfError { get; private set; }

    /// <summary>The session id of the per-row upload form being submitted.</summary>
    [BindProperty] public int UploadSessionId { get; set; }
    [BindProperty] public IFormFile? Pdf { get; set; }

    public SelectList TypeOptions => new(
        Enum.GetValues<SessionType>().Select(t => new { Value = t, Text = Display(t) }),
        "Value", "Text");

    public static string Display(SessionType t) => t switch
    {
        SessionType.Keynote => "Keynote",
        SessionType.TechnicalSession => "Technical Session",
        SessionType.MasterClass => "Master Class",
        SessionType.AskTheExperts => "Ask the Experts",
        SessionType.PanelDiscussion => "Panel Discussion",
        SessionType.Welcome => "Welcome",
        _ => t.ToString(),
    };

    /// <summary>Render a 1–5 rating as a smiley (for the average / per-comment display).</summary>
    public static string Face(double? rating)
    {
        if (rating is null) return "—";
        return (int)Math.Round(rating.Value) switch
        {
            <= 1 => "😞",
            2 => "🙁",
            3 => "😐",
            4 => "🙂",
            _ => "😀",
        };
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Rooms = await _svc.ListRoomsAsync(me.EventId, ct);
        Dashboard = await _svc.BuildDashboardAsync(me.EventId, FilterType, FilterRoom, ct);
        await LoadPdfSectionAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §166 — store the uploaded final evaluation PDF for one session on SharePoint, point
    /// <see cref="Session.EvaluationFormUrl"/> at the HUB PROXY url (never a SharePoint link),
    /// and email the session's speaker(s). Organizer-only write (<see cref="OrganizerAuth.IsRealOrganizer"/>).
    /// </summary>
    public async Task<IActionResult> OnPostUploadPdfAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // Re-bind the dashboard + list so the page renders fully on any return path.
        Rooms = await _svc.ListRoomsAsync(me.EventId, ct);
        Dashboard = await _svc.BuildDashboardAsync(me.EventId, FilterType, FilterRoom, ct);

        if (!_pdf.CanManage)
        {
            PdfError = "The final-evaluation-PDF SharePoint folder is not configured yet — nothing was uploaded.";
            await LoadPdfSectionAsync(me.EventId, ct);
            return Page();
        }

        // The session must be a real non-service session in this edition.
        var session = await _db.Sessions.FirstOrDefaultAsync(
            s => s.Id == UploadSessionId && s.EventId == me.EventId && !s.IsServiceSession, ct);
        if (session is null)
        {
            PdfError = "That session was not found in this edition.";
            await LoadPdfSectionAsync(me.EventId, ct);
            return Page();
        }

        if (Pdf is null || Pdf.Length == 0)
        {
            PdfError = "Choose a PDF file to upload.";
            await LoadPdfSectionAsync(me.EventId, ct);
            return Page();
        }
        if (!IsPdf(Pdf))
        {
            PdfError = "Only PDF files are accepted for the final session evaluation.";
            await LoadPdfSectionAsync(me.EventId, ct);
            return Page();
        }

        using var ms = new MemoryStream();
        await Pdf.CopyToAsync(ms, ct);
        await _pdf.UploadAsync(session.Id, ms.ToArray(), ct);

        // The link handed to speakers is the HUB PROXY url — NEVER the SharePoint URL
        // (speakers have no SharePoint access; this is the bug fixed for graphics §160).
        session.EvaluationFormUrl = SessionEvalPdfService.ProxyUrlFor(session.Id);
        session.EvaluationEmailedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Best-effort: notify the speaker(s). A send failure must NOT fail the upload.
        var notified = await NotifySpeakersAsync(me.EventId, session.Id, session.Title, ct);

        PdfMessage = notified > 0
            ? $"Uploaded the evaluation PDF for \"{session.Title}\" and emailed {notified} speaker(s)."
            : $"Uploaded the evaluation PDF for \"{session.Title}\".";
        await LoadPdfSectionAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadPdfSectionAsync(int eventId, CancellationToken ct)
    {
        PdfConfigured = _pdf.CanManage;
        PdfSessions = await _pdf.ListSessionsAsync(eventId, ct);
    }

    /// <summary>Email each speaker on the session that their evaluation is ready. Returns the count attempted.</summary>
    private async Task<int> NotifySpeakersAsync(int eventId, int sessionId, string title, CancellationToken ct)
    {
        var speakers = await _pdf.GetSpeakerContactsAsync(eventId, sessionId, ct);
        if (speakers.Count == 0) return 0;

        var mySessionsUrl = $"{Request.Scheme}://{Request.Host}/Speaker";
        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

        const string subject = "[ELDK27] Your session evaluation is ready";
        foreach (var sp in speakers)
        {
            var html =
                $"<p>Hi {Enc(sp.FullName.Split(' ').FirstOrDefault())},</p>"
                + $"<p>The final evaluation for your session <b>{Enc(title)}</b> is now available.</p>"
                + $"<p style=\"margin:18px 0 4px;\"><a href=\"{Enc(mySessionsUrl)}\" "
                + "style=\"display:inline-block;padding:12px 22px;background:#008BD2;color:#ffffff;"
                + "font-weight:700;font-size:15px;border-radius:6px;text-decoration:none;\">"
                + "Open My Sessions</a></p>"
                + "<p style=\"color:#6a7280;font-size:13px;\">Find the \"Evaluations\" download on your "
                + "My Sessions page.</p>";
            try { await _email.SendAsync(sp.Email, subject, html, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "SessionEval PDF: notify {To} failed.", sp.Email); }
        }
        return speakers.Count;
    }

    private static bool IsPdf(IFormFile file)
    {
        var name = file.FileName ?? string.Empty;
        return name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);
    }
}
