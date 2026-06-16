using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// People hub — a thin Organizer-gated card-link landing (phase-1 nav/IA
/// consolidation). Fans out to the people-management feature pages
/// (Participants, Pre-selection queue, Onboarding, Attendees, Action queue);
/// it holds no data of its own. Auth mirrors /Organizer/SponsorAdmin/Index:
/// signed-in Organizer only, otherwise AccessDenied = true (a friendly notice,
/// not a 401, so the chrome still renders).
/// </summary>
[Authorize]
public class PeopleModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;

    public PeopleModel(ICurrentParticipantAccessor participant)
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
