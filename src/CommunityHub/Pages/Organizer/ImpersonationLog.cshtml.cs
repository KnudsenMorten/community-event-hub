using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Read-only review of the acting-as audit trail (organizer + secretary-token
/// sessions: start / return / on-behalf writes). Organizer-only.
/// </summary>
[Authorize]
public class ImpersonationLogModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ImpersonationAuditService _audit;

    public ImpersonationLogModel(
        ICurrentParticipantAccessor participant, ImpersonationAuditService audit)
    {
        _participant = participant;
        _audit = audit;
    }

    public bool AccessDenied { get; private set; }
    public IReadOnlyList<ImpersonationAudit> Entries { get; private set; } = new List<ImpersonationAudit>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer || me.IsActingAs)
        {
            AccessDenied = true;
            return Page();
        }

        Entries = await _audit.RecentAsync(me.EventId, take: 200, ct: ct);
        return Page();
    }
}
