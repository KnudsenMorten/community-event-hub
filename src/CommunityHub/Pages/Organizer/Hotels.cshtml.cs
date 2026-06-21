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
    private readonly HotelBulkOperationService _bulk;

    public HotelsModel(
        ICurrentParticipantAccessor participant,
        HotelManagementService hotels,
        HotelBulkOperationService bulk)
    {
        _participant = participant;
        _hotels = hotels;
        _bulk = bulk;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public IReadOnlyList<Hotel> Hotels { get; private set; } = Array.Empty<Hotel>();

    /// <summary>The hotel ids ticked in the bulk-select grid (posted form field).</summary>
    [BindProperty] public List<int> SelectedIds { get; set; } = new();

    [BindProperty(SupportsGet = true)] public int? EditId { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Address { get; set; }
    [BindProperty] public string? ContactEmail { get; set; }
    [BindProperty] public string? Notes { get; set; }

    /// <summary>Reserved room-block size (blank = not set / clear the block).</summary>
    [BindProperty] public int? RoomBlockSize { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (!await TryLoadAsync(me.EventId, ct)) return Page();

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
                RoomBlockSize = hotel.RoomBlockSize;
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
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        try
        {
            if (EditId is int id && id > 0)
            {
                var ok = await _hotels.UpdateHotelAsync(
                    me.EventId, id, Name ?? "", Address, ContactEmail, Notes, RoomBlockSize, ct);
                Message = ok ? "Hotel updated." : "Hotel not found.";
            }
            else
            {
                await _hotels.CreateHotelAsync(
                    me.EventId, Name ?? "", Address, ContactEmail, Notes, RoomBlockSize, ct);
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
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var ok = await _hotels.DeleteHotelAsync(me.EventId, id, ct);
        Message = ok ? "Hotel deleted (assigned people were un-assigned)." : "Hotel not found.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Bulk-delete the ticked hotels (REQUIREMENTS §20 universal CRUD + bulk). The
    /// safe semantics are the single-row ones applied row by row in
    /// <see cref="HotelBulkOperationService"/>: every placed participant is
    /// un-assigned first so no foreign key dangles, then the hotels are removed in
    /// one transaction. The honest banner reports deleted / un-assigned / not-found.
    /// Organizer-only, edition-scoped; the page's confirm modal (live count) gates
    /// the click.
    /// </summary>
    public async Task<IActionResult> OnPostBulkDeleteAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var requested = SelectedIds.Where(id => id > 0).Distinct().Count();
        if (requested == 0)
        {
            Error = "Pick at least one hotel first.";
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var result = await _bulk.DeleteAsync(me.EventId, SelectedIds, ct);
        var skipped = result.Skipped(requested);

        if (result.Deleted == 0)
        {
            Error = "No matching hotels were found in this edition.";
        }
        else
        {
            Message = $"{result.Deleted} hotel(s) deleted"
                + (result.Unassigned > 0
                    ? $" ({result.Unassigned} person(s) were un-assigned)"
                    : string.Empty)
                + (skipped > 0 ? $", {skipped} not found" : string.Empty)
                + ".";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Hotels = await _hotels.ListHotelsAsync(eventId, ct);
    }

    /// <summary>
    /// Load the hotel list, but never let a data-layer failure take the whole
    /// page down with an unhandled 500. The Hotels grid SELECTs every Hotel
    /// column (incl. the newer <c>RoomBlockSize</c>); if the DEV/PROD schema
    /// ever lags the deployed code (a migration not yet applied, a stale read
    /// replica, a transient SQL blip), the bare query would throw and the page
    /// returned HTTP 500 — which is exactly what the iPhone-SE post-deploy
    /// validation caught on <c>/Organizer/Hotels</c>. Degrade to an honest
    /// error banner on a 200 page instead: the organizer sees a clear message,
    /// the route stays alive, and the auto-migrate on the next boot heals the
    /// schema. Returns false when the load failed (caller renders the banner).
    /// </summary>
    private async Task<bool> TryLoadAsync(int eventId, CancellationToken ct)
    {
        try
        {
            await LoadAsync(eventId, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw; // a cancelled request is not a page error — let it unwind.
        }
        catch (Exception)
        {
            Hotels = Array.Empty<Hotel>();
            Error = "The hotel list could not be loaded right now. Please refresh in a moment.";
            return false;
        }
    }
}
