using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sponsors;

/// <summary>
/// PUBLIC, no-login sponsors page (REQUIREMENTS § 7). Lists the active edition's
/// sponsor companies <b>grouped by tier</b> (highest first), each with their logo,
/// the resolved public company name, and an optional link. Sponsors are public, so
/// there is no publish gate — every sponsor company on file is shown. Read-only —
/// there is no write path to abuse.
///
/// Mobile-first (~360px) + a11y (semantic per-tier sections, logo alt text / monogram
/// fallback, <c>role="status"</c> count). Empty states for "no live event" and
/// "no sponsors yet". Data built by <see cref="PublicSponsorsService"/>.
/// </summary>
[AllowAnonymous]
public class IndexModel : PageModel
{
    private readonly PublicSponsorsService _svc;

    public IndexModel(PublicSponsorsService svc) => _svc = svc;

    public PublicSponsorsView? View { get; private set; }

    /// <summary>True when there is no active event (distinct from "no sponsors yet").</summary>
    public bool NoActiveEvent { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        View = await _svc.BuildAsync(ct);
        NoActiveEvent = View is null;
        return Page();
    }
}
