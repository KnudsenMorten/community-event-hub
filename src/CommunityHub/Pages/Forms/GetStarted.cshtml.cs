using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Generic "Get started" WIZARD (REQUIREMENTS §43, design A) — one guided, resumable
/// entry point for the roles that don't have a bespoke wizard (Volunteer, Organizer,
/// Media, EventPartner). It chains the participant's initial tasks (Profile →
/// role-specific steps → entitlement logistics) with a progress bar, showing ONLY
/// the steps the participant is ENTITLED to (§44a) and reading completion from each
/// page's persisted data (§44b — a saved value = done). A SHELL over the existing
/// pages (their save/validation/entitlement logic is reused untouched), so it is
/// low-risk and always reflects current data on refresh. Speakers/Sponsors are sent
/// to their own dedicated wizards.
/// </summary>
[Authorize]
public class GetStartedModel : PageModel
{
    private readonly RoleWizardService _wizard;
    private readonly ICurrentParticipantAccessor _participant;

    public GetStartedModel(RoleWizardService wizard, ICurrentParticipantAccessor participant)
    {
        _wizard = wizard;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }
    public RoleWizardView? View { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Roles with a bespoke wizard are routed to it (single canonical "Get started").
        if (me.Role == ParticipantRole.Speaker) return RedirectToPage("/Forms/SpeakerWizard");
        if (me.Role == ParticipantRole.Sponsor) return RedirectToPage("/Sponsor/GetStarted");

        if (!RoleWizardService.Handles(me.Role)) { AccessDenied = true; return Page(); }

        View = await _wizard.BuildAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }
}
