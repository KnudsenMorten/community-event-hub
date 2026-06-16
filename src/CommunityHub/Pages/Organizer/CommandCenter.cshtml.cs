using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// The organizer command-center landing (REQUIREMENTS §20 Organizer). A single
/// "what needs my attention" glance built from REAL data by
/// <see cref="CommandCenterService"/>: registrations, onboarding completion % per
/// persona, who-hasn't-done-what, hotel/swag/lunch/dinner headcounts, session +
/// sponsor status, and today's / overdue tasks. Every stat tile is clickable and
/// deep-links into the relevant filtered grid; the page shows a "last updated"
/// time. Read-only — it never posts back. Organizer-gated server-side.
/// </summary>
[Authorize]
public class CommandCenterModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommandCenterService _commandCenter;

    public CommandCenterModel(
        ICurrentParticipantAccessor participant,
        CommandCenterService commandCenter)
    {
        _participant = participant;
        _commandCenter = commandCenter;
    }

    public bool AccessDenied { get; private set; }
    public CommandCenterSnapshot? Snapshot { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Snapshot = await _commandCenter.BuildAsync(me.EventId, ct);
        return Page();
    }
}
