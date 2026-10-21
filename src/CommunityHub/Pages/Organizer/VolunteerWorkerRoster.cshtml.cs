using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1297 — the WORKER ROSTER: the list of active volunteers, and which of them the planner may
/// auto-assign from.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-22: <i>"i was think of a view under volunteers, where i see a list of
/// active volunteers and then i add the ones in scope, that will not attend conference and can
/// help without 50/50 considerations."</i></para>
///
/// <para>🔑 <b>Membership IS the flag.</b> There is no per-person checkbox hidden on another
/// screen — being on this list is what puts somebody in the auto-planner's pool, and everyone
/// else is hand-placed through the ordinary manual assignment path.</para>
///
/// <para>🔑 <b>Each person's stated availability renders beside the toggle on purpose.</b> Adding
/// somebody who answered "Morning only" to a full-day pool is a contradiction, and this is where
/// it should be visible — rather than discovered later as an unexplained shortfall.</para>
///
/// <para>Organizer-only (server-enforced in <see cref="VolunteerPlanningRosterService"/> as well
/// as here), mobile-first (~360px).</para>
/// </remarks>
[Authorize]
public class VolunteerWorkerRosterModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VolunteerPlanningRosterService _roster;

    public VolunteerWorkerRosterModel(
        ICurrentParticipantAccessor participant,
        VolunteerPlanningRosterService roster)
    {
        _participant = participant;
        _roster = roster;
    }

    public bool AccessDenied { get; private set; }
    public IReadOnlyList<RosterRow> Rows { get; private set; } = Array.Empty<RosterRow>();
    [TempData] public string? Message { get; set; }

    public int InScope => Rows.Count(r => r.InRoster);
    public int Total => Rows.Count;

    private VolunteerStructureService.ActorContext Actor(CurrentParticipant me)
        => new(me.ParticipantId, me.Email, me.Role, me.EventId, me.IsActingAs);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) { AccessDenied = true; return Page(); }

        Rows = await _roster.LoadAsync(Actor(me), ct);
        return Page();
    }

    [CommunityHub.Audit.Audit("Added a volunteer to the worker roster", TargetType = "VolunteerPlanningRoster")]
    public async Task<IActionResult> OnPostAddAsync(int participantId, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        try
        {
            await _roster.AddAsync(Actor(me), participantId, note, ct);
            Message = "Added to the worker roster — the planner will now auto-assign them.";
        }
        catch (VolunteerValidationException ex) { Message = ex.Message; }
        catch (VolunteerAccessDeniedException) { return Forbid(); }

        return RedirectToPage();
    }

    [CommunityHub.Audit.Audit("Removed a volunteer from the worker roster", TargetType = "VolunteerPlanningRoster")]
    public async Task<IActionResult> OnPostRemoveAsync(int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        try
        {
            await _roster.RemoveAsync(Actor(me), participantId, ct);
            // ⚠️ Said explicitly: taking somebody out of scope does NOT unassign them. Removing a
            // person from a list looks like it should, and quietly dropping their shifts — with the
            // mail that goes with it — is not something a scope list should be able to do.
            Message = "Removed from the worker roster. Future planner runs skip them; "
                    + "any assignments they already hold are unchanged.";
        }
        catch (VolunteerAccessDeniedException) { return Forbid(); }

        return RedirectToPage();
    }
}
