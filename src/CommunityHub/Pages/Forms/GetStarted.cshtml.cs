using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Generic "Get started" entry (REQUIREMENTS §43 + §148) for the roles WITHOUT a bespoke
/// wizard (Volunteer, Organizer, Media, EventPartner). This was the link-list that sent the
/// participant OUT to each standalone form page; it is now a THIN ROUTER to the generic
/// in-wizard stepper host (<see cref="WizardModel"/> at <c>/Forms/Wizard</c>), which asks
/// <see cref="RoleWizardService"/> for the SAME ordered, entitlement-gated, done-marked plan
/// and renders each step's fields INLINE (a true stepper) — the gates/ordering are untouched.
///
/// <para>Speakers and Sponsors are sent to their own dedicated entries
/// (<c>/Forms/SpeakerWizard</c> and <c>/Sponsor/GetStarted</c>). A role with no generic wizard
/// (<see cref="RoleWizardService.Handles"/> is false, e.g. Attendee) still sees the
/// access-denied note rendered by this page. Kept (not deleted) so the nav entry
/// (<c>Nav.GetStarted</c> → <c>/Forms/GetStarted</c>) and deep links stay alive.</para>
/// </summary>
[Authorize]
public class GetStartedModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;

    public GetStartedModel(ICurrentParticipantAccessor participant)
    {
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }

    public IActionResult OnGet()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Roles with a bespoke wizard are routed to it (single canonical "Get started").
        if (me.Role == ParticipantRole.Speaker) return RedirectToPage("/Forms/SpeakerWizard");
        if (me.Role == ParticipantRole.Sponsor) return RedirectToPage("/Sponsor/GetStarted");

        // A role this generic wizard does not serve (e.g. Attendee) sees the access note.
        if (!RoleWizardService.Handles(me.Role)) { AccessDenied = true; return Page(); }

        // §148: the generic roles now flow through the true in-wizard stepper host.
        return RedirectToPage("/Forms/Wizard");
    }
}
