using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Volunteers hub — a thin Organizer-gated card-link landing (phase-1 nav/IA
/// consolidation). It fans out to the existing feature pages and holds no data
/// of its own. Auth mirrors /Organizer/SponsorAdmin/Index: signed-in Organizer
/// only, otherwise AccessDenied = true (a friendly notice, not a 401).
/// </summary>
[Authorize]
public class VolunteersModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;

    public VolunteersModel(ICurrentParticipantAccessor participant)
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
