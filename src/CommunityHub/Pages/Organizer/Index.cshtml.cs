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

        // §498/§499 — this card claims "the hub is already reminding them", so it must list the
        // people the hub ACTUALLY reminds. It listed every attendee row with an open mismatch,
        // including someone whose ticket had been cancelled — and since §499b those people are
        // explicitly no longer reminded, which made the sentence untrue.
        //
        // Exclude the DROPPED-OUT (was in, now out). Deliberately NOT "keep only remindable": an
        // attendee who has simply not been provisioned yet has no participant row at all, and
        // dropping them would hide real outstanding work rather than a departed person.
        var droppedOutEmails = await _db.Participants
            .Where(p => p.EventId == me.EventId)
            .DroppedOut()
            .Select(p => p.Email.ToLower())
            .ToListAsync(ct);

        Mismatches = await _db.Attendees
            .Where(a => a.EventId == me.EventId && a.HasReconciliationMismatch
                        && !droppedOutEmails.Contains(a.Email.ToLower()))
            .OrderBy(a => a.LastName)
            .ToListAsync(ct);
        OpenActionItems = await _actions.CountOpenAsync(me.EventId, ct);
        return Page();
    }
}
