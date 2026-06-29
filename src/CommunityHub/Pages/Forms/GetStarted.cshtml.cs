using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Generic "Get started" wizard (REQUIREMENTS §43 + §161) for the roles WITHOUT a bespoke
/// wizard — Volunteer, Organizer, Media, EventPartner. §161 makes it the SAME sponsor-style
/// STEPPER landing page as the speaker + sponsor pages: a progress bar + EVERY entitled step
/// as a card with its Done/Pending state and an Edit/Open link into that step's form, even at
/// 100% (no "all done — go to hub" dead-end). It asks <see cref="RoleWizardService"/> for the
/// SAME ordered, entitlement-gated, done-marked plan and renders it through the shared
/// <c>_WizardStepper</c> partial all three pages use, so they can never drift.
///
/// <para>Speakers and Sponsors are routed to their own entries (<c>/Forms/SpeakerWizard</c>
/// and <c>/Sponsor/GetStarted</c>). A role with no generic wizard
/// (<see cref="RoleWizardService.Handles"/> is false, e.g. Attendee) sees the access note.</para>
/// </summary>
[Authorize]
public class GetStartedModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly RoleWizardService _wizard;

    public GetStartedModel(ICurrentParticipantAccessor participant, RoleWizardService wizard)
    {
        _participant = participant;
        _wizard = wizard;
    }

    public bool AccessDenied { get; private set; }
    public RoleWizardView? View { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Roles with a bespoke wizard are routed to their canonical "Get started".
        if (me.Role == ParticipantRole.Speaker) return RedirectToPage("/Forms/SpeakerWizard");
        if (me.Role == ParticipantRole.Sponsor) return RedirectToPage("/Sponsor/GetStarted");

        // A role this generic wizard does not serve (e.g. Attendee) sees the access note.
        if (!RoleWizardService.Handles(me.Role)) { AccessDenied = true; return Page(); }

        View = await _wizard.BuildAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }
}
