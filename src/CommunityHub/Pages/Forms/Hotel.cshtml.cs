using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Notify;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Hotel-preference form (CONTEXT.md section 9). The participant submits or
/// updates one HotelBooking per edition. Edits are blocked after the edition
/// lock date (Event.LockDate) - the form goes read-only.
/// </summary>
[Authorize]
public class HotelModel : PageModel
{
    /// <summary>SourceKey prefix used for the "complete the hotel form" task.</summary>
    public const string HotelTaskKey = "hotel-form";

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly HotelCalendarInviter _inviter;
    private readonly ILogger<HotelModel> _logger;

    public HotelModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        HotelCalendarInviter inviter,
        ILogger<HotelModel> logger)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _inviter = inviter;
        _logger = logger;
    }

    // bool? so radio "true"/"false" is honored; null = user hasn't chosen yet.
    [BindProperty] public bool? NeedsRoom { get; set; }
    [BindProperty] public DateOnly? CheckInDate { get; set; }
    [BindProperty] public DateOnly? CheckOutDate { get; set; }
    [BindProperty] public string? RoomShareWith { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public bool IsLocked { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        IsLocked = await IsEditingLockedAsync(me.EventId, ct);
        await EnsureHotelTaskExistsAsync(me.EventId, me.ParticipantId, ct);

        var existing = await _db.HotelBookings.FirstOrDefaultAsync(
            h => h.EventId == me.EventId && h.ParticipantId == me.ParticipantId,
            ct);
        if (existing is not null)
        {
            NeedsRoom = existing.NeedsRoom;
            CheckInDate = existing.CheckInDate;
            CheckOutDate = existing.CheckOutDate;
            RoomShareWith = existing.RoomShareWith;
            Notes = existing.Notes;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        if (await IsEditingLockedAsync(me.EventId, ct))
        {
            IsLocked = true;
            Message = "Editing is closed for this event.";
            return Page();
        }

        // Explicit choice required: user must pick Yes or No.
        if (NeedsRoom is null)
        {
            Message = "Please choose 'Yes, I need a hotel room' or 'No, I don't need a hotel'.";
            return Page();
        }
        // If Yes, dates are mandatory.
        if (NeedsRoom == true && (CheckInDate is null || CheckOutDate is null))
        {
            Message = "Check-in and check-out dates are required when you need a hotel room.";
            return Page();
        }

        var booking = await _db.HotelBookings.FirstOrDefaultAsync(
            h => h.EventId == me.EventId && h.ParticipantId == me.ParticipantId,
            ct);

        if (booking is null)
        {
            booking = new HotelBooking
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.HotelBookings.Add(booking);
        }
        else
        {
            booking.UpdatedAt = _clock.GetUtcNow();
        }

        // Choice is validated above; safe to deref the nullable bool.
        booking.NeedsRoom    = NeedsRoom!.Value;
        booking.CheckInDate  = NeedsRoom == true ? CheckInDate  : null;
        booking.CheckOutDate = NeedsRoom == true ? CheckOutDate : null;
        booking.RoomShareWith = NeedsRoom == true ? RoomShareWith : null;
        booking.Notes = Notes;

        await _db.SaveChangesAsync(ct);
        await MarkHotelTaskDoneAsync(me.EventId, me.ParticipantId, ct);
        Message = "Your hotel preference has been saved.";

        // Send (or refresh) the calendar invite when the participant has
        // actually picked dates. Same stable UID per (participant, event), so
        // re-saves UPDATE the recipient's existing calendar entry. Re-issued
        // again as "[CONFIRMED]" by the organizer-side hotel import.
        if (booking.NeedsRoom && booking.CheckInDate is not null && booking.CheckOutDate is not null
            && booking.CheckInDate < booking.CheckOutDate)
        {
            try
            {
                var eventCode = await _db.Events
                    .Where(e => e.Id == me.EventId)
                    .Select(e => e.Code)
                    .FirstOrDefaultAsync(ct) ?? "Event Hub";

                await _inviter.SendAsync(
                    eventCode: eventCode,
                    toEmail: me.Email,
                    fullName: me.FullName,
                    checkInDate: booking.CheckInDate.Value,
                    checkOutDate: booking.CheckOutDate.Value,
                    confirmed: booking.ConfirmationState == HotelConfirmationState.Confirmed,
                    confirmationNumber: booking.ConfirmationNumber,
                    roomType: booking.RoomType,
                    participantId: me.ParticipantId,
                    eventId: me.EventId,
                    ct: ct);

                booking.CalendarInviteSentAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                Message += " A calendar invitation has been emailed to you (marked [NOT CONFIRMED] until the hotel confirms).";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not send hotel calendar invite to {Email}", me.Email);
                Message += $" (Calendar invite could not be sent: {ex.Message})";
            }
        }
        return Page();
    }

    // ----- Auto-task: "Complete the Hotel form" --------------------------
    private async Task EnsureHotelTaskExistsAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{HotelTaskKey}:{participantId}";
        if (await _db.Tasks.AnyAsync(
                t => t.EventId == eventId
                     && t.AssignedParticipantId == participantId
                     && t.SourceKey == sourceKey, ct)) return;

        var due = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (DateOnly?)e.StartDate.AddDays(-30))
            .FirstOrDefaultAsync(ct);

        _db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId,
            AssignedParticipantId = participantId,
            Title = "Complete the Hotel form",
            Description = "Tell us if you need a hotel room and pick your check-in/check-out dates. " +
                          "Saving the form marks this task Done.",
            DueDate = due,
            State = TaskState.Open,
            SourceKey = sourceKey,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkHotelTaskDoneAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{HotelTaskKey}:{participantId}";
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null || task.State == TaskState.Done) return;
        task.State = TaskState.Done;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>True once the edition's lock date has passed.</summary>
    private async Task<bool> IsEditingLockedAsync(int eventId, CancellationToken ct)
    {
        var lockDate = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => e.LockDate)
            .FirstOrDefaultAsync(ct);
        if (lockDate is null) return false;
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        return today > lockDate.Value;
    }
}
