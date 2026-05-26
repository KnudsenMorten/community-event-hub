using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer view of all participants for the edition, with role and
/// active/inactive filtering (CONTEXT.md - participants sometimes drop out, so
/// IsActive must be visible and toggleable). Organizer-only. Toggling IsActive
/// is how a dropped-out participant is deactivated - an inactive participant
/// can no longer sign in (the PIN flow checks IsActive).
/// </summary>
[Authorize]
public class ParticipantsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;

    public ParticipantsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant)
    {
        _db = db;
        _participant = participant;
    }

    public List<Participant> Participants { get; private set; } = new();
    public bool AccessDenied { get; private set; }

    /// <summary>Filter: "all", "active", "inactive".</summary>
    [BindProperty(SupportsGet = true)]
    public string ActiveFilter { get; set; } = "active";

    /// <summary>Filter by role, or null for all roles.</summary>
    [BindProperty(SupportsGet = true)]
    public ParticipantRole? RoleFilter { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = true;
            return Page();
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Toggle a participant's IsActive flag.</summary>
    public async Task<IActionResult> OnPostToggleActiveAsync(
        int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = true;
            return Page();
        }

        var target = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == participantId && p.EventId == me.EventId, ct);
        if (target is not null)
        {
            target.IsActive = !target.IsActive;
            await _db.SaveChangesAsync(ct);
        }

        return RedirectToPage(new { ActiveFilter, RoleFilter });
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var query = _db.Participants
            .Where(p => p.EventId == eventId);

        query = ActiveFilter switch
        {
            "active" => query.Where(p => p.IsActive),
            "inactive" => query.Where(p => !p.IsActive),
            _ => query, // "all"
        };

        if (RoleFilter is not null)
        {
            query = query.Where(p => p.Role == RoleFilter.Value);
        }

        Participants = await query
            .OrderBy(p => p.Role)
            .ThenBy(p => p.FullName)
            .ToListAsync(ct);
    }
}
