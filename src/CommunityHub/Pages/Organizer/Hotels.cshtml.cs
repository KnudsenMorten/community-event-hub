using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Notify;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

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
    private readonly CommunityHubDbContext _db;
    private readonly HotelCalendarInviter _inviter;
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;
    private readonly CommunityHub.Core.Settings.RingResolver _rings;
    private readonly ILogger<HotelsModel> _logger;

    public HotelsModel(
        ICurrentParticipantAccessor participant,
        HotelManagementService hotels,
        HotelBulkOperationService bulk,
        CommunityHubDbContext db,
        HotelCalendarInviter inviter,
        CommunityHub.Core.Settings.FeatureGateService gate,
        CommunityHub.Core.Settings.RingResolver rings,
        ILogger<HotelsModel> logger)
    {
        _participant = participant;
        _hotels = hotels;
        _bulk = bulk;
        _db = db;
        _inviter = inviter;
        _gate = gate;
        _rings = rings;
        _logger = logger;
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

    /// <summary>
    /// Booking CONFIRMATION NUMBER from the hotel (REQUIREMENTS §46). Blank while
    /// the hotel hasn't confirmed; once an organizer enters it, the reservation is
    /// CONFIRMED and everyone placed here is re-sent their calendar invite.
    /// </summary>
    [BindProperty] public string? ConfirmationNumber { get; set; }

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
                ConfirmationNumber = hotel.ConfirmationNumber;
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
                // Did this hotel already carry a confirmation number BEFORE the save?
                // We compare to decide whether the reservation just became CONFIRMED
                // (an organizer entered the number for the first time / changed it),
                // which is what re-issues the calendar invites (REQUIREMENTS §46).
                var before = await _hotels.GetHotelAsync(me.EventId, id, ct);
                var hadNumber = !string.IsNullOrWhiteSpace(before?.ConfirmationNumber);
                var newNumber = (ConfirmationNumber ?? "").Trim();
                var nowSet = newNumber.Length > 0;
                var numberChanged = before is not null
                    && !string.Equals((before.ConfirmationNumber ?? "").Trim(), newNumber, StringComparison.Ordinal);

                var ok = await _hotels.UpdateHotelAsync(
                    me.EventId, id, Name ?? "", Address, ContactEmail, Notes, RoomBlockSize,
                    ConfirmationNumber, ct);
                Message = ok ? "Hotel updated." : "Hotel not found.";

                // Reservation just became / re-confirmed → re-send the CONFIRMED
                // calendar invite to everyone placed in this hotel who needs a room.
                if (ok && nowSet && numberChanged)
                {
                    var (sent, skipped) = await ReissueConfirmedInvitesAsync(
                        me.EventId, id, Name ?? "", Address, newNumber, ct);
                    Message += $" Reservation is now [CONFIRMED] (number {newNumber}).";
                    Message += sent > 0
                        ? $" Updated calendar invites sent to {sent} reserver(s)"
                          + (skipped > 0 ? $", {skipped} skipped (above released ring)" : "")
                          + "."
                        : (skipped > 0
                            ? $" No invites sent ({skipped} reserver(s) above the released ring)."
                            : " No reservers needed a room here yet.");
                }
            }
            else
            {
                await _hotels.CreateHotelAsync(
                    me.EventId, Name ?? "", Address, ContactEmail, Notes, RoomBlockSize,
                    ConfirmationNumber, ct);
                Message = "Hotel added.";
                if (!string.IsNullOrWhiteSpace(ConfirmationNumber))
                {
                    Message += " Reservation is [CONFIRMED]. (No one is placed in this hotel yet, so no invites were sent.)";
                }
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

    /// <summary>
    /// Re-issue the hotel calendar invite as [CONFIRMED] to everyone placed in
    /// <paramref name="hotelId"/> who needs a room, carrying the new confirmation
    /// number (REQUIREMENTS §46). These recipients ARE participants, so the normal
    /// participant email path applies: each is filtered through the hotel-invite
    /// feature's released ring (same gate as hotel assignment), and the invite is
    /// sent via the ring-governed <see cref="HotelCalendarInviter"/> (DEV-redirected)
    /// reusing the stable per-(participant,event) UID, so the recipient's existing
    /// calendar entry UPDATES in place rather than duplicating. Returns how many
    /// invites were sent and how many were skipped because the recipient is above
    /// the released ring. One failed send never aborts the rest.
    /// </summary>
    private async Task<(int Sent, int Skipped)> ReissueConfirmedInvitesAsync(
        int eventId, int hotelId, string hotelName, string? hotelAddress,
        string confirmationNumber, CancellationToken ct)
    {
        var reservers = await _hotels.ListReserversForInviteAsync(eventId, hotelId, ct);
        if (reservers.Count == 0) return (0, 0);

        var eventCode = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => e.Code)
            .FirstOrDefaultAsync(ct) ?? "Event Hub";

        var sent = 0;
        var skipped = 0;
        foreach (var r in reservers)
        {
            // Ring-scoped (REQUIREMENTS §23a): don't re-invite a participant above
            // the hotel-invite feature's released ring. Broad default = everyone.
            if (!await _gate.IsTargetInReleasedRingAsync(
                    "hotel-invite", eventId, r.ParticipantId, _rings, ct))
            {
                skipped++;
                continue;
            }

            try
            {
                // hotelConfirmationNumber non-blank → the builder renders [CONFIRMED]
                // + the number; the inviter sets the gated hotel-invite EmailContext.
                await _inviter.SendAsync(
                    eventCode: eventCode,
                    toEmail: r.Email,
                    fullName: r.FullName,
                    checkInDate: r.CheckInDate,
                    checkOutDate: r.CheckOutDate,
                    confirmed: true,
                    confirmationNumber: confirmationNumber,
                    roomType: null,
                    participantId: r.ParticipantId,
                    eventId: eventId,
                    ct: ct,
                    hotelName: string.IsNullOrWhiteSpace(hotelName) ? null : hotelName,
                    hotelAddress: hotelAddress,
                    hotelConfirmationNumber: confirmationNumber);

                // Stamp the booking so the participant-side form knows the invite
                // was re-issued (best-effort; never blocks the rest).
                var booking = await _db.HotelBookings.FirstOrDefaultAsync(
                    b => b.EventId == eventId && b.ParticipantId == r.ParticipantId, ct);
                if (booking is not null)
                {
                    booking.ConfirmationState = HotelConfirmationState.Confirmed;
                    booking.ConfirmationNumber = confirmationNumber;
                    booking.ConfirmedAt = DateTimeOffset.UtcNow;
                    booking.CalendarInviteSentAt = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not re-send CONFIRMED hotel invite to {Email} (participant {Pid})",
                    r.Email, r.ParticipantId);
            }
        }
        return (sent, skipped);
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
