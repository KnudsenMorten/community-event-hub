using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §834 — the SoMe SETUP wizard, mirroring the sponsor/speaker Get Started flow he named as the
/// pattern. Four steps, computed live, organizer-only.
///
/// <para>🔒 Setup-only: once the four are done the page says so and steps back, because the engine
/// autobuilds everything after that (§834.2).</para>
/// </summary>
[Authorize]
public class SoMeSetupModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SoMeWizardService _wizard;

    public SoMeSetupModel(ICurrentParticipantAccessor participant, SoMeWizardService wizard)
    {
        _participant = participant;
        _wizard = wizard;
    }

    public bool AccessDenied { get; private set; }
    public SoMeWizardView? View { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        View = await _wizard.BuildAsync(me.EventId, ct);
        return Page();
    }
}
