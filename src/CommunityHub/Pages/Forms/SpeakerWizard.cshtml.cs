using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Speaker onboarding entry (REQUIREMENTS §28 + §148). This was the link-list "wizard"
/// that sent the speaker OUT to each standalone form page; it is now a THIN REDIRECT to the
/// generic in-wizard stepper host (<see cref="WizardModel"/> at <c>/Forms/Wizard</c>). The host
/// asks <see cref="CommunityHub.Forms.SpeakerWizardService"/> for the SAME ordered,
/// entitlement-gated, done-marked plan and renders each step's fields INLINE with
/// Previous / Save&amp;next / "Step X of N" (a true stepper) — the ordering/gates are untouched.
///
/// <para>The page is kept (not deleted) so the existing nav entry
/// (<c>Nav.SpeakerOnboarding</c> → <c>/Forms/SpeakerWizard</c>) and any deep links stay alive.</para>
/// </summary>
[Authorize]
public class SpeakerWizardModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Forms/Wizard");
}
