using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ⚰️ §784.7(C) — MERGED INTO <c>/Organizer/SessionFeedback</c>. This is a redirect, on purpose.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-03: *"it is confusing now"* — the sessions list, the results, the QR codes
/// and the report state were four pages holding five facts about ONE session. They are now one row
/// each on Session feedback.</para>
///
/// <para>🔒 <b>The page is kept as a redirect rather than deleted</b> (§784.7 build constraint:
/// *"Leave redirects from the old URLs; he has them in his history and in /Organizer/Setup"*). A
/// bookmark that 404s after a consolidation reads as "you broke it", not as "it moved".</para>
///
/// <para>The functionality lives on: the CEH sync is the <b>Sync sessions from CEH</b> action, what
/// it replaced is still reported, and "which sessions cannot collect" became the
/// <c>not collecting</c> chip with its reason named in words.</para>
/// </remarks>
[Authorize]
public class EvaluationSessionsModel : PageModel
{
    public IActionResult OnGet() => RedirectToPagePermanent("/Organizer/SessionFeedback");
}
