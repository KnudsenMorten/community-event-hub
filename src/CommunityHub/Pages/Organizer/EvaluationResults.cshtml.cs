using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ⚰️ §784.7(C) — MERGED INTO <c>/Organizer/SessionFeedback</c>. This is a redirect, on purpose.
/// </summary>
/// <remarks>
/// <para>The event roll-up (pooled index, mean session, unattributed responses) and the per-session
/// figures moved verbatim, presentation rules included: the score is an <b>index</b> and never
/// carries a <c>%</c> sign, the <b>band always accompanies the number</b>, and a below-threshold
/// session shows progress toward @MinimumResponses rather than a volatile number.</para>
///
/// <para>🔒 Kept as a redirect, not deleted — §784.7: *"Leave redirects from the old URLs; he has
/// them in his history and in /Organizer/Setup"*. The report DOWNLOAD moved with it and is now
/// <c>?handler=Report&amp;evaluationSessionId=</c> on the merged page.</para>
/// </remarks>
[Authorize]
public class EvaluationResultsModel : PageModel
{
    public IActionResult OnGet() => RedirectToPagePermanent("/Organizer/SessionFeedback");

    /// <summary>
    /// The old report link was <c>/Organizer/EvaluationResults?handler=Report&amp;sessionId=</c>.
    /// Kept so an existing link still downloads instead of 404ing — it forwards to the merged page's
    /// handler with the same id (both are the EVALUATION session id).
    /// </summary>
    public IActionResult OnGetReport(int sessionId) =>
        RedirectToPagePermanent(
            "/Organizer/SessionFeedback", "Report", new { evaluationSessionId = sessionId });
}
