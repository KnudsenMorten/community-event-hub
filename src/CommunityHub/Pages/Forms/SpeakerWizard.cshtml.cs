using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Speaker "Get started" WIZARD (REQUIREMENTS §28 + §161) — the sponsor-style STEPPER
/// landing page for speakers: a progress bar + EVERY entitled step shown as a card with its
/// Done/Pending state and an Edit/Open link into that step's form, even at 100% (no
/// "all done — go to hub" dead-end). It asks <see cref="SpeakerWizardService"/> for the SAME
/// ordered, entitlement-gated, done-marked plan it always built and renders it through the
/// shared <c>_WizardStepper</c> partial that the role + sponsor pages also use, so the three
/// "Get started" pages can never drift.
///
/// <para>§161 superseded the §148 thin-redirect (which sent speakers straight into the
/// one-step-at-a-time inline host and dead-ended on completion). The standalone step pages /
/// the inline host stay reachable; this page is just the checklist over them.</para>
/// </summary>
[Authorize]
public class SpeakerWizardModel : PageModel
{
    private readonly SpeakerWizardService _wizard;
    private readonly ICurrentParticipantAccessor _participant;

    public SpeakerWizardModel(SpeakerWizardService wizard, ICurrentParticipantAccessor participant)
    {
        _wizard = wizard;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }
    public SpeakerWizardView? View { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { AccessDenied = true; return Page(); }

        View = await _wizard.BuildAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }
}
