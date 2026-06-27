using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Attendee;

/// <summary>
/// Attendee "Master Class Q&amp;A" shortcut (operator 2026-06-24): resolves the
/// signed-in attendee's CONFIRMED Master Class and redirects to its
/// <c>/MasterClassPage/{sessionId}</c> page — the one that actually hosts the
/// Q&amp;A: the group comment board (visible to everyone in the class + replies)
/// and the attendee's own 1:1 questions to the speakers. A signed-in confirmed
/// attendee is recognised there by email (no token needed). Falls back to the
/// Master Class selection page when the attendee has no confirmed seat yet.
/// </summary>
[Authorize]
public class MasterClassQaModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly MasterClassSignupService _svc;

    public MasterClassQaModel(
        ICurrentParticipantAccessor participant,
        MasterClassSignupService svc)
    {
        _participant = participant;
        _svc = svc;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var a = await _svc.ResolveByEmailAsync(me.EventId, me.Email, ct);
        if (a is not null)
        {
            var mine = await _svc.GetForAttendeeAsync(a.EventId, a.Id, ct);
            var confirmed = mine.FirstOrDefault(s => s.Status == MasterClassSignupStatus.Confirmed);
            if (confirmed is not null)
            {
                // Go straight to the Q&A host page for the confirmed session.
                return Redirect($"/MasterClassPage/{confirmed.SessionId}");
            }
        }

        // No confirmed seat — flash an INFO notice (a no-op redirect, not a green
        // "done") via the shared TempData flash so /Attendee/Index actually shows it.
        TempData["Flash"] = "Reserve a Master Class seat first — its Q&A opens once you're confirmed.";
        TempData["FlashKind"] = "info";
        return RedirectToPage("/Attendee/Index");
    }
}
