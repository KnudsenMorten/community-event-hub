using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1146e — the volunteer availability overview, on its own page.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-28: <i>"i asked for a page Volunteer Availability button here where the
/// overview should be … add a new page and move the overview under that"</i>.</para>
///
/// <para>🔑 <b>The hub is a hub.</b> /Organizer/Volunteers is a card-link landing that holds no data
/// of its own — printing a table of every volunteer under the tiles made the one page you scan to
/// choose a destination into the longest page in the area. The overview is a destination, so it gets
/// a door like the others.</para>
///
/// <para>🔒 The grid itself is unchanged and still comes from
/// <see cref="CommunityHub.Core.Volunteers.VolunteerAvailabilityOverviewService"/> via the shared
/// partial — the same builder the pre-selection queue uses. Moving where it renders must not fork
/// what it renders.</para>
/// </remarks>
[Authorize]
public class VolunteerAvailabilityModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHub.Core.Volunteers.VolunteerAvailabilityOverviewService _availability;

    public VolunteerAvailabilityModel(
        ICurrentParticipantAccessor participant,
        CommunityHub.Core.Volunteers.VolunteerAvailabilityOverviewService availability)
    {
        _participant = participant;
        _availability = availability;
    }

    public bool AccessDenied { get; private set; }

    public CommunityHub.Core.Volunteers.VolunteerAvailabilityOverviewService.Overview? Availability
    { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        // null ⇒ every volunteer in the edition, including the ones already onboarded. The
        // pre-selection queue narrows to its own rows; this page is the whole crew.
        Availability = await _availability.BuildAsync(me.EventId, participantIds: null, ct);
        return Page();
    }
}
