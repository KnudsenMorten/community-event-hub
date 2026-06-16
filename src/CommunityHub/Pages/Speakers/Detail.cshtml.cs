using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Speakers;

/// <summary>
/// PUBLIC, no-login detail page for a single speaker (<c>/Speakers/{id}</c>,
/// REQUIREMENTS § 6 — "never publish an unselected speaker"). Shows the speaker's
/// photo/monogram, tagline, bio, and their session(s), each cross-linked to the
/// public session-detail page.
///
/// <b>HARD GATE.</b> Enforced in <see cref="PublicSpeakersService.GetByIdAsync"/>:
/// the page 404s for any participant who is not a selected (<c>SelectedForPublish</c>),
/// active, speaker-role profile in the active edition — so an unselected, withdrawn,
/// or non-speaker id can never leak a profile. Mobile-first (~360px) + a11y.
/// </summary>
[AllowAnonymous]
public class DetailModel : PageModel
{
    private readonly PublicSpeakersService _svc;

    public DetailModel(PublicSpeakersService svc) => _svc = svc;

    public PublicSpeakerDetail? Speaker { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        Speaker = await _svc.GetByIdAsync(id, ct);
        if (Speaker is null) return NotFound();
        return Page();
    }
}
