using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer-gated room-block occupancy view (REQUIREMENTS §3 hotels — "manage the
/// room block per hotel" / §20 Organizer Logistics). Read-only: shows, per hotel,
/// the reserved block size against the people placed there (and how many of those
/// actually need a room), with an at-a-glance under / at / over-block state and a
/// roll-up of total block vs. room-needers + the people who need a room but are
/// not yet placed. The actual placing + recording the block size lives on
/// <c>/Organizer/Hotels</c> + <c>/Organizer/HotelAssignments</c>.
/// </summary>
[Authorize]
public class HotelRoomBlocksModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly HotelRoomBlockService _blocks;

    public HotelRoomBlocksModel(
        ICurrentParticipantAccessor participant,
        HotelRoomBlockService blocks)
    {
        _participant = participant;
        _blocks = blocks;
    }

    public bool AccessDenied { get; private set; }
    public HotelBlockSnapshot Snapshot { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Snapshot = await _blocks.BuildAsync(me.EventId, ct);
        return Page();
    }
}
