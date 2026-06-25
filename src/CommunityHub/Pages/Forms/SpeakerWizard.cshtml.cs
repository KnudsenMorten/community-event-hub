using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Speaker onboarding WIZARD (REQUIREMENTS §28, design A) — one guided, resumable
/// entry point that chains a speaker's initial tasks (Speaker Details → Hotel →
/// Dinner → Lunch → Swag → Travel) with a progress bar, showing only the steps the
/// speaker is ENTITLED to. It is a SHELL over the existing form pages (their
/// save/validation/entitlement logic is reused untouched), so it is low-risk and
/// always reflects current data on refresh. Speaker-only.
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
    public string FullName { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { AccessDenied = true; return Page(); }

        FullName = me.FullName;
        View = await _wizard.BuildAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }
}
