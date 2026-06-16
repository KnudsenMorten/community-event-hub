using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer-gated page to assign each participant to a hotel, set the per-person
/// hotel confirmation number, and view everyone grouped by hotel (Hotel 1 list,
/// Hotel 2 list, … plus a "Not assigned" group) so room blocks can be managed per
/// hotel (REQUIREMENTS §3 multi-hotel management).
/// </summary>
[Authorize]
public class HotelAssignmentsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly HotelManagementService _hotels;

    public HotelAssignmentsModel(ICurrentParticipantAccessor participant, HotelManagementService hotels)
    {
        _participant = participant;
        _hotels = hotels;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    public IReadOnlyList<Hotel> AllHotels { get; private set; } = Array.Empty<Hotel>();
    public IReadOnlyList<HotelGroup> Groups { get; private set; } = Array.Empty<HotelGroup>();

    [BindProperty] public int ParticipantId { get; set; }
    [BindProperty] public int? HotelId { get; set; }
    [BindProperty] public string? ConfirmationNumber { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAssignAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var ok = await _hotels.AssignParticipantAsync(me.EventId, ParticipantId, HotelId, ct);
        Message = ok ? "Hotel assignment saved." : "Participant or hotel not found.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var ok = await _hotels.SetConfirmationNumberAsync(me.EventId, ParticipantId, ConfirmationNumber, ct);
        Message = ok ? "Confirmation number saved." : "Participant not found.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        AllHotels = await _hotels.ListHotelsAsync(eventId, ct);
        Groups = await _hotels.GroupByHotelAsync(eventId, ct);
    }
}
