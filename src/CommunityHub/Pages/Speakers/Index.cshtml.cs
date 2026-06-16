using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Speakers;

/// <summary>
/// PUBLIC, no-login speaker lineup (REQUIREMENTS § 6 — "never publish an unselected
/// speaker"). Lists the active edition's speakers with photo + tagline + their linked
/// session(s).
///
/// <b>HARD GATE.</b> Only speakers an organizer has explicitly selected for publish
/// (<c>SpeakerProfile.SelectedForPublish == true</c>, active, speaker-role) appear —
/// enforced in <see cref="PublicSpeakersService"/>. The flag defaults to false for
/// everyone, so until the lineup is selected the page renders a graceful
/// "lineup coming soon" empty state; selected speakers appear automatically once the
/// flag is flipped. Read-only — there is no write path to abuse.
///
/// Mobile-first (~360px) + a11y (semantic list, <c>role="status"</c> count, photo
/// alt text). Empty states for "no live event" and "lineup not selected yet".
/// </summary>
[AllowAnonymous]
public class IndexModel : PageModel
{
    private readonly PublicSpeakersService _svc;

    public IndexModel(PublicSpeakersService svc) => _svc = svc;

    public PublicSpeakersView? View { get; private set; }

    /// <summary>True when there is no active event (distinct from "lineup not selected yet").</summary>
    public bool NoActiveEvent { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        View = await _svc.BuildAsync(ct);
        NoActiveEvent = View is null;
        return Page();
    }
}
