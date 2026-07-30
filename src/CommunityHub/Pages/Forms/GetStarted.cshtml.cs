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
    private readonly AttendeeWizardService _attendeeWizard;

    public GetStartedModel(
        ICurrentParticipantAccessor participant,
        RoleWizardService wizard,
        AttendeeWizardService attendeeWizard)
    {
        _participant = participant;
        _wizard = wizard;
        _attendeeWizard = attendeeWizard;
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

        // §207/§208: attendees get their own ticket-driven stepper (Master Class + Party for
        // 2-day; Party for 1-day), rendered through the SAME shared stepper.
        if (me.Role == ParticipantRole.Attendee)
        {
            View = await _attendeeWizard.BuildAsync(me.EventId, me.ParticipantId, ct);
            return Page();
        }

        // A role this generic wizard does not serve sees the access note.
        if (!RoleWizardService.Handles(me.Role)) { AccessDenied = true; return Page(); }

        View = await _wizard.BuildAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }
}
