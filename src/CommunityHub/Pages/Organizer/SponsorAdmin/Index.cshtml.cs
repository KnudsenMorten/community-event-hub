using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer.SponsorAdmin;

/// <summary>
/// Sponsor Admin landing page -- single Organizer-gated menu hub that
/// links to the three sponsor-facing admin surfaces:
///   1. Tasks   -- create / delete / change deadline of sponsor tasks
///   2. Leads   -- view + download per-sponsor leads pulled from Zoho
///   3. Status  -- per-sponsor dashboard of task completion + lead volume
///
/// Auth pattern matches the existing /Organizer/Index.cshtml: require
/// a signed-in Participant whose Role == Organizer; everyone else gets
/// AccessDenied = true and a friendly notice (NOT a 401), so the page
/// still renders enough chrome that the user can navigate away.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;

    public IndexModel(ICurrentParticipantAccessor participant)
    {
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }

    public IActionResult OnGet()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        return Page();
    }
}
