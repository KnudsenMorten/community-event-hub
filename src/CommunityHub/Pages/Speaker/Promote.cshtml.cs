using CommunityHub.Auth;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// Speaker "Help to promote your session(s)" Get-Started step (REQUIREMENTS §116).
/// Drives LinkedIn promotion of the speaker's session(s): the wired publish path
/// lives on <see cref="GraphicsModel"/> (Speaker/Graphics →
/// <c>SpeakerLinkedInPublishService</c>, §52) using the pulled session graphics, and
/// this step surfaces a prefilled LinkedIn share + the #ELDK27 #ExpertsLiveDK tags
/// (§115). Completion is a MANUAL mark-done (promoting is external/optional), tracked
/// on a per-speaker <c>promote:</c> <see cref="CommunityHub.Core.Domain.ParticipantTask"/>. Speaker-only.
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_PromoteFields</c> partial and delegates load + the manual mark-done to
/// <see cref="PromoteFormService"/>. The SAME service backs the inline wizard step
/// (<c>PromoteStepHandler</c>), so the standalone page and the wizard behave identically. The
/// page is still deep-linked from My Tasks / emails, so its behavior is unchanged.</para>
/// </summary>
[Authorize]
public class PromoteModel : PageModel
{
    private readonly PromoteFormService _promote;
    private readonly ICurrentParticipantAccessor _participant;

    public PromoteModel(PromoteFormService promote, ICurrentParticipantAccessor participant)
    {
        _promote = promote;
        _participant = participant;
    }

    /// <summary>The shared render model rendered by the <c>_PromoteFields</c> partial
    /// (carries NotSpeaker / Done / the prefilled LinkedIn share URL).</summary>
    public PromoteFormModel Form { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Form = await _promote.LoadAsync(me.EventId, me.ParticipantId, me.Role, ct);
        return Page();
    }

    /// <summary>Toggle the manual "promote your session(s)" completion.</summary>
    public async Task<IActionResult> OnPostToggleAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Form = await _promote.ToggleAsync(me.EventId, me.ParticipantId, me.Role, ct);
        if (Form.NotSpeaker) return Page();   // non-speakers: "this page is for speakers" view, no toggle
        return RedirectToPage();
    }
}
