using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Volunteer;

/// <summary>
/// SUPERSEDED (§234 UX): the standalone volunteer "My tasks" page was merged into the
/// unified <c>/volunteer/myschedule</c> (see <see cref="MyScheduleModel"/>, REQUIREMENTS
/// §20/§21) — the set-status / raise-help handlers all live there now. This route only
/// remains so old links keep working: it PERMANENTLY redirects to My schedule instead of
/// rendering a drift-prone duplicate of the same UI.
/// </summary>
[Authorize]
public class MyTasksModel : PageModel
{
    public IActionResult OnGet() => RedirectToPagePermanent("/Volunteer/MySchedule");
}
