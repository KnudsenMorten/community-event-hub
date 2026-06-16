using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer-gated CRUD for the edition's <see cref="Hotel"/> rows
/// (REQUIREMENTS §3 multi-hotel management). Create / edit / delete the hotels
/// attendees can be split across; assignment + grouping lives on
/// <c>/Organizer/HotelAssignments</c>.
/// </summary>
[Authorize]
public class HotelsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly HotelManagementService _hotels;

    public HotelsModel(ICurrentParticipantAccessor participant, HotelManagementService hotels)
    {
        _participant = participant;
        _hotels = hotels;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<Hotel> Hotels { get; private set; } = Array.Empty<Hotel>();

    [BindProperty(SupportsGet = true)] public int? EditId { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Address { get; set; }
    [BindProperty] public string? ContactEmail { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);

        // Prefill the form when editing an existing hotel.
        if (EditId is int id && id > 0)
        {
            var hotel = Hotels.FirstOrDefault(h => h.Id == id);
            if (hotel is not null)
            {
                Name = hotel.Name;
                Address = hotel.Address;
                ContactEmail = hotel.ContactEmail;
                Notes = hotel.Notes;
            }
            else
            {
                EditId = null; // stale link — fall back to the add form
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        try
        {
            if (EditId is int id && id > 0)
            {
                var ok = await _hotels.UpdateHotelAsync(me.EventId, id, Name ?? "", Address, ContactEmail, Notes, ct);
                Message = ok ? "Hotel updated." : "Hotel not found.";
            }
            else
            {
                await _hotels.CreateHotelAsync(me.EventId, Name ?? "", Address, ContactEmail, Notes, ct);
                Message = "Hotel added.";
            }
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var ok = await _hotels.DeleteHotelAsync(me.EventId, id, ct);
        Message = ok ? "Hotel deleted (assigned people were un-assigned)." : "Hotel not found.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Hotels = await _hotels.ListHotelsAsync(eventId, ct);
    }
}
