using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ⚰️ §784.7(A) — MERGED INTO <c>/Organizer/EvaluationDevices</c>. This is a redirect, on purpose.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-03: the two pages become ONE titled <b>Evaluation Devices</b>, with
/// onboarding as a section rather than a page of its own. They were never two jobs — they were the
/// same job (get the fleet working) at two moments, and the split meant approving a unit here and
/// then going somewhere else to see whether it ever arrived.</para>
///
/// <para>🔴 <b>The security property moved intact.</b> The approval queue is still the control on the
/// only endpoint in the system that accepts an unauthenticated write: a unit that has asked to join
/// has no key, no upload URL and can post no responses until an organizer approves it. It is now a
/// section of the devices page — same rule, one page.</para>
///
/// <para>🔒 Kept as a redirect, not deleted — §784.7: *"Leave redirects from the old URLs"*.</para>
/// </remarks>
[Authorize]
public class EvaluationOnboardingModel : PageModel
{
    public IActionResult OnGet() => RedirectToPagePermanent("/Organizer/EvaluationDevices");
}
