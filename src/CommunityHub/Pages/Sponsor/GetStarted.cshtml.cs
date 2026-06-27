using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// Sponsor "Get started" WIZARD (REQUIREMENTS §32) — the guided shell over the
/// Company Details sections (Company info → Event coordinator → Contacts → Logos →
/// Booth members → Booth materials), mirroring the speaker onboarding wizard. Shows
/// progress over the hub-tracked steps and deep-links each step into Company Details.
/// Sponsor-only.
/// </summary>
[Authorize]
public class GetStartedModel : PageModel
{
    private readonly SponsorWizardService _wizard;
    private readonly ICurrentParticipantAccessor _participant;

    public GetStartedModel(SponsorWizardService wizard, ICurrentParticipantAccessor participant)
    {
        _wizard = wizard;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }
    public bool NoCompanyLink { get; private set; }
    public SponsorWizardView? View { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Sponsor) { AccessDenied = true; return Page(); }

        View = await _wizard.BuildAsync(me.EventId, me.ParticipantId, ct);
        if (View is null) NoCompanyLink = true;
        return Page();
    }
}
