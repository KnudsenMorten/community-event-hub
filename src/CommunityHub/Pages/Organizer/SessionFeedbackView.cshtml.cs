using CommunityHub.Auth;
using CommunityHub.Core.Evaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1291 — one session's feedback ON THE PAGE: score, the four answers, what people picked, every
/// comment. The same data as the PDF, from the same builder, so the two can never disagree.
/// </summary>
/// <remarks>
/// Operator 2026-09-21: *"how can i click on a session and see all feedbacks"*, then asked for an
/// on-page view instead of the PDF. Organizer-only, like the page it is opened from: the comments are
/// the most sensitive data in the system (visible to the session's own speakers and organizers only).
/// </remarks>
[Authorize]
public class SessionFeedbackViewModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EvaluationReportBuilder _builder;

    public SessionFeedbackViewModel(ICurrentParticipantAccessor participant, EvaluationReportBuilder builder)
    {
        _participant = participant;
        _builder = builder;
    }

    public bool AccessDenied { get; private set; }
    public int EvaluationSessionId { get; private set; }
    public EvaluationReportService.ReportData? Data { get; private set; }

    public async Task<IActionResult> OnGetAsync(int evaluationSessionId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        EvaluationSessionId = evaluationSessionId;
        // Scoped to the organizer's own edition by the builder (another edition's id is "not found").
        var report = await _builder.BuildAsync(me.EventId, evaluationSessionId, ct);
        if (report is null) return NotFound();
        Data = report.Data;
        return Page();
    }

    /// <summary>Danish wall time.</summary>
    public static string Danish(DateTimeOffset t, string format) =>
        TimeZoneInfo.ConvertTime(t, CommunityHub.Core.Integrations.EventTimezone.Tz).ToString(format);

    /// <summary>A whole-number share, never NaN.</summary>
    public static string Pct(int n, int total) => total == 0 ? "0" : Math.Round(100d * n / total).ToString("0");
}
