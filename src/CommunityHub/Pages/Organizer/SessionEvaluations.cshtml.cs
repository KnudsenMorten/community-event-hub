using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ⚰️ §784.7(C) — MERGED INTO <c>/Organizer/SessionFeedback</c>. This is a redirect, on purpose.
/// </summary>
/// <remarks>
/// <para>This page supplied the <b>report state</b> column of the merge (§784.7's own table names it
/// as the source), so folding it in is what makes one row answer everything about a session. Its two
/// published-PDF links moved with it — still through the <b>hub proxy</b>, never a SharePoint link —
/// alongside the speaker names, which were the reason to open it.</para>
///
/// <para>🔒 <b>§783.9 still holds and must not be undone here or there:</b> the state is
/// <c>released</c> / <c>not released</c> and the ENGINE is the only writer. The manual upload was
/// retired because a hand-uploaded file is silently overwritten the next time the engine publishes
/// to the same deterministic name — two writers, one name, no conflict detection. Do not
/// reintroduce it on the merged page.</para>
///
/// <para>🔒 Kept as a redirect, not deleted — §784.7: *"Leave redirects from the old URLs"*.
/// <c>/Organizer/Sessions</c> links here too.</para>
/// </remarks>
[Authorize]
public class SessionEvaluationsModel : PageModel
{
    public IActionResult OnGet() => RedirectToPagePermanent("/Organizer/SessionFeedback");
}
