using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// The organizer area (CONTEXT.md 9z). Surfaces the attendee reconciliation
/// mismatches for a human to review - the hub flags them but never
/// auto-merges identities. Organizer-only.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly OrganizerActionItemService _actions;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        OrganizerActionItemService actions)
    {
        _db = db;
        _participant = participant;
        _actions = actions;
    }

    public List<Core.Domain.Attendee> Mismatches { get; private set; } = new();
    public int OpenActionItems { get; private set; }
    public bool AccessDenied { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Organizer-only page.
        if (me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = true;
            return Page();
        }

        Mismatches = await _db.Attendees
            .Where(a => a.EventId == me.EventId && a.HasReconciliationMismatch)
            .OrderBy(a => a.LastName)
            .ToListAsync(ct);
        OpenActionItems = await _actions.CountOpenAsync(me.EventId, ct);
        return Page();
    }
}
