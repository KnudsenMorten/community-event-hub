using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1386 — one guest's hotel reservation, edited by an organizer in ONE place: needs a room, dates, hotel,
/// roommate, guests in room, state, confirmation + cancellation number, notes. Every change is logged.
/// 🔒 Sends no mail to the guest.
/// </summary>
[Authorize]
public class HotelReservationModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly HotelReservationEditor _editor;

    public HotelReservationModel(ICurrentParticipantAccessor participant, HotelReservationEditor editor)
    {
        _participant = participant;
        _editor = editor;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    public HotelReservationView? View { get; private set; }

    [BindProperty(SupportsGet = true)] public int Pid { get; set; }
    [BindProperty] public bool NeedsRoom { get; set; }
    [BindProperty] public DateOnly? CheckIn { get; set; }
    [BindProperty] public DateOnly? CheckOut { get; set; }
    [BindProperty] public int? HotelId { get; set; }
    [BindProperty] public int? RoommateId { get; set; }
    [BindProperty] public int? GuestsInRoom { get; set; }
    [BindProperty] public HotelConfirmationState State { get; set; }
    [BindProperty] public string? ConfirmationNumber { get; set; }
    [BindProperty] public string? CancellationNumber { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public async Task<IActionResult> OnGetAsync(string? msg, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) { AccessDenied = true; return Page(); }
        Message = msg;
        View = await _editor.LoadAsync(me.EventId, Pid, ct);
        if (View is null) return NotFound();
        NeedsRoom = View.NeedsRoom;
        CheckIn = View.CheckIn;
        CheckOut = View.CheckOut;
        HotelId = View.HotelId;
        RoommateId = View.RoommateParticipantId;
        GuestsInRoom = View.GuestsInRoom;
        State = View.State;
        ConfirmationNumber = View.ConfirmationNumber;
        CancellationNumber = View.CancellationNumber;
        Notes = View.Notes;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        Error = await _editor.SaveAsync(me.EventId, Pid, new HotelReservationInput(
            NeedsRoom, CheckIn, CheckOut, HotelId, RoommateId, GuestsInRoom, ConfirmationNumber, State,
            CancellationNumber, Notes), me.Email, ct);
        if (Error is null) return RedirectToPage(new { pid = Pid, msg = "Saved. The change is in the hotel change log; no mail went to the guest." });

        View = await _editor.LoadAsync(me.EventId, Pid, ct);
        return Page();
    }
}
