using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Resources;
using CommunityHub.Forms;
using CommunityHub.Notify;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

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
    private readonly OrganizerActionItemService _actions;
    private readonly ILogger<HotelModel> _logger;
    private readonly IStringLocalizer<SharedResource> _loc;

    public HotelModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        HotelCalendarInviter inviter,
        OrganizerActionItemService actions,
        ILogger<HotelModel> logger,
        IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _inviter = inviter;
        _actions = actions;
        _logger = logger;
        _loc = loc;
    }

    // bool? so radio "true"/"false" is honored; null = user hasn't chosen yet.
    [BindProperty] public bool? NeedsRoom { get; set; }
    [BindProperty] public DateOnly? CheckInDate { get; set; }
    [BindProperty] public DateOnly? CheckOutDate { get; set; }
    [BindProperty] public string? RoomShareWith { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public bool IsLocked { get; private set; }
    public string? Message { get; private set; }

    /// <summary>REQUIREMENTS §51 — when this hotel booking was last saved (UpdatedAt); null = never saved.</summary>
    public DateTimeOffset? LastSavedAt { get; private set; }

    /// <summary>
    /// The booking confirmation number for the participant's assigned hotel
    /// (REQUIREMENTS §46). Read-only on this form — organizers set it on
    /// <c>/Organizer/Hotels</c>; null/blank until the hotel confirms. When set, the
    /// reservation is [CONFIRMED] and the participant has been re-sent an updated
    /// calendar invite carrying this number.
    /// </summary>
    public string? HotelConfirmationNumber { get; private set; }

    /// <summary>The name of the hotel the participant is placed in (for the read-only confirmation block).</summary>
    public string? AssignedHotelName { get; private set; }

    /// <summary>True once the assigned hotel carries a confirmation number — the reservation is CONFIRMED.</summary>
    public bool IsReservationConfirmed => !string.IsNullOrWhiteSpace(HotelConfirmationNumber);

    /// <summary>The signed-in participant's role — drives the role-specific hotel policy text.</summary>
    public ParticipantRole Role { get; private set; }

    /// <summary>
    /// Hotel is arranged + covered by us for crew/speakers/organizers; sponsors and
    /// attendees arrange their own accommodation, so the form is not shown to them.
    ///
    /// <para>FEATURE B: additionally gated by ENTITLEMENT
    /// (<see cref="OrderItem.Hotel"/>). A sponsor-self-funded speaker (entitled to no
    /// hotel) is treated as "not relevant" even though their role is Speaker; a
    /// Supported speaker is entitled. Every NON-speaker role keeps its historical
    /// access (Organizer / Volunteer / Media / EventPartner), so access is never
    /// silently removed. Computed in <see cref="OnGetAsync"/> / <see cref="OnPostAsync"/>.</para>
    /// </summary>
    public bool HotelRelevant { get; private set; }

    /// <summary>The role-only relevance rule (excludes sponsors + attendees) — the historical gate.</summary>
    private static bool HotelRoleRelevant(ParticipantRole role) =>
        role is not (ParticipantRole.Sponsor or ParticipantRole.Attendee);

    /// <summary>FEATURE B eligibility: entitled to a hotel, OR a non-speaker role that historically had the form.</summary>
    private async Task<bool> IsHotelRelevantAsync(CurrentParticipant me, CancellationToken ct)
    {
        var entitled = await FormEntitlementGate.IsEntitledAsync(
            _db, me.EventId, me.ParticipantId, OrderItem.Hotel, ct);
        var historicalNonSpeaker =
            me.Role != ParticipantRole.Speaker && HotelRoleRelevant(me.Role);
        return entitled || historicalNonSpeaker;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        Role = me.Role;
        HotelRelevant = await IsHotelRelevantAsync(me, ct);
        if (!HotelRelevant) return Page();   // not entitled / sponsors / attendees: "not relevant" view, no form

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
            LastSavedAt = existing.UpdatedAt;
        }

        // Read-only reservation confirmation (REQUIREMENTS §46): surface the assigned
        // hotel's booking confirmation number + name. Set by organizers; the
        // participant only views it. The hotel-level number wins; if it is blank we
        // fall back to the per-person number an organizer may have set in assignments.
        var placement = await _db.Participants
            .Where(p => p.Id == me.ParticipantId && p.EventId == me.EventId)
            .Select(p => new
            {
                HotelName = p.Hotel != null ? p.Hotel.Name : null,
                HotelLevelNumber = p.Hotel != null ? p.Hotel.ConfirmationNumber : null,
                PerPersonNumber = p.HotelConfirmationNumber,
            })
            .FirstOrDefaultAsync(ct);
        if (placement is not null)
        {
            AssignedHotelName = placement.HotelName;
            HotelConfirmationNumber = !string.IsNullOrWhiteSpace(placement.HotelLevelNumber)
                ? placement.HotelLevelNumber
                : placement.PerPersonNumber;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        Role = me.Role;
        HotelRelevant = await IsHotelRelevantAsync(me, ct);
        if (!HotelRelevant) return Page();   // not entitled / sponsors / attendees can't book through us

        if (await IsEditingLockedAsync(me.EventId, ct))
        {
            IsLocked = true;
            Message = "Editing is closed for this event.";
            return Page();
        }

        // Field-level validation (REQUIREMENTS §21 shared validation pattern).
        // Explicit choice required: user must pick Yes or No.
        if (NeedsRoom is null)
        {
            ModelState.AddModelError(nameof(NeedsRoom), _loc["Hotel.ErrPickNeed"]);
        }
        else if (NeedsRoom == true)
        {
            // If Yes, dates are mandatory and check-out must be after check-in.
            if (CheckInDate is null)
            {
                ModelState.AddModelError(nameof(CheckInDate), _loc["Hotel.ErrCheckIn"]);
            }
            if (CheckOutDate is null)
            {
                ModelState.AddModelError(nameof(CheckOutDate), _loc["Hotel.ErrCheckOut"]);
            }
            if (CheckInDate is not null && CheckOutDate is not null && CheckOutDate <= CheckInDate)
            {
                ModelState.AddModelError(nameof(CheckOutDate), _loc["Hotel.ErrCheckOrder"]);
            }
        }
        if (!ModelState.IsValid)
        {
            // Re-render with field errors; nothing is persisted.
            return Page();
        }

        var booking = await _db.HotelBookings.FirstOrDefaultAsync(
            h => h.EventId == me.EventId && h.ParticipantId == me.ParticipantId,
            ct);

        var isNewBooking = booking is null;
        if (booking is null)
        {
            booking = new HotelBooking
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
                UpdatedAt = _clock.GetUtcNow(),
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

        // Late-change alert: changing an ALREADY-submitted booking inside the
        // window before the lock date is something organizers must re-confirm
        // with the hotel. First-time submissions and early edits stay quiet.
        if (!isNewBooking)
        {
            var summary = booking.NeedsRoom
                ? $"Hotel changed to {booking.CheckInDate:dd MMM} → {booking.CheckOutDate:dd MMM}"
                  + (string.IsNullOrWhiteSpace(booking.RoomShareWith) ? "" : $", sharing with {booking.RoomShareWith}")
                : "Hotel changed to: no room needed";
            await _actions.RaiseIfLateAsync(
                me.EventId, OrganizerActionItemService.TypeHotelChanged,
                me.ParticipantId, summary, ct);
        }

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

                // Pull the participant's organizer-assigned hotel (multi-hotel
                // placement) + their per-person confirmation number so the email +
                // calendar invite name the actual hotel, its address and number.
                var placement = await _db.Participants
                    .Where(p => p.Id == me.ParticipantId && p.EventId == me.EventId)
                    .Select(p => new
                    {
                        HotelName = p.Hotel != null ? p.Hotel.Name : null,
                        HotelAddress = p.Hotel != null ? p.Hotel.Address : null,
                        p.HotelConfirmationNumber,
                    })
                    .FirstOrDefaultAsync(ct);

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
                    ct: ct,
                    hotelName: placement?.HotelName,
                    hotelAddress: placement?.HotelAddress,
                    hotelConfirmationNumber: placement?.HotelConfirmationNumber);

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
        task.CompletedAt = _clock.GetUtcNow();
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
