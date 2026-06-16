using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Ends an organizer "act-as" session and restores the organizer's own login.
/// Only valid from an organizer-kind acting-as session (the marker carries the
/// organizer's own participant id); a secretary-token session has no organizer
/// to return to and is rejected.
/// </summary>
[Authorize]
public class ReturnToOrganizerModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly ImpersonationAuditService _audit;
    private readonly TimeProvider _clock;

    public ReturnToOrganizerModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db,
        ImpersonationAuditService audit,
        TimeProvider clock)
    {
        _participant = participant;
        _db = db;
        _audit = audit;
        _clock = clock;
    }

    public IActionResult OnGet() => RedirectToPage("/Organizer/Participants");

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Only an organizer-kind acting-as session can return; it carries the
        // organizer's own participant id in the actor marker.
        if (me.ActingAs is null
            || me.ActingAs.Kind != ImpersonationActorKind.Organizer
            || me.ActingAs.ActorParticipantId is null)
        {
            return RedirectToPage("/Index");
        }

        var organizer = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == me.ActingAs.ActorParticipantId.Value
                 && p.EventId == me.EventId, ct);
        if (organizer is null || organizer.Role != ParticipantRole.Organizer)
        {
            // The acting organizer no longer exists / is no longer an organizer:
            // fail safe by signing out entirely.
            await HttpContext.SignOutAsync();
            return RedirectToPage("/Login");
        }

        await _audit.RecordAsync(
            me.EventId, ImpersonationActorKind.Organizer,
            actorParticipantId: organizer.Id, actorLabel: me.ActingAs.ActorLabel,
            targetParticipantId: me.ParticipantId,
            action: ImpersonationAuditService.ActionReturn,
            detail: "Organizer returned from acting-as session.",
            ct: ct);

        await ImpersonationSignIn.ReturnToOrganizerAsync(HttpContext, organizer, _clock);
        return RedirectToPage("/Organizer/Participants");
    }
}
