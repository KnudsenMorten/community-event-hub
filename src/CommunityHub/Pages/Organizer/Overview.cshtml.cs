using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer cross-role overview (REQUIREMENTS §11). A single read-only page that
/// surfaces participation, task completion, speaker milestone progress, volunteer
/// coverage, sponsor totals, attendee check-in and the "needs attention" items —
/// all computed live by <see cref="OrganizerOverviewService"/>. No writes; this
/// page never posts back.
/// </summary>
[Authorize]
public class OverviewModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly OrganizerOverviewService _overview;

    public OverviewModel(
        ICurrentParticipantAccessor participant,
        OrganizerOverviewService overview)
    {
        _participant = participant;
        _overview = overview;
    }

    public bool AccessDenied { get; private set; }
    public OrganizerOverview? Report { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Report = await _overview.BuildAsync(me.EventId, ct);
        return Page();
    }
}
