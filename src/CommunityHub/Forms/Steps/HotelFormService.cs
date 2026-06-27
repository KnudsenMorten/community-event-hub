using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Resources;
using CommunityHub.Notify;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Hotel step (REQUIREMENTS §148). It is shared by the
/// standalone <c>/Forms/Hotel</c> page AND the inline wizard step, and is the model the
/// <c>_HotelFields</c> partial binds to. The EDITABLE fields (top of the class) are the
/// only ones model binding fills; the DISPLAY fields are <see cref="BindNeverAttribute"/>
/// and are populated by <see cref="HotelFormService"/> (load + save), never from the POST.
/// </summary>
public sealed class HotelFormModel
{
    // ----- editable (bound from the POST) --------------------------------
    // bool? so radio "true"/"false" is honored; null = user hasn't chosen yet.
    public bool? NeedsRoom { get; set; }
    public DateOnly? CheckInDate { get; set; }
    public DateOnly? CheckOutDate { get; set; }
    public string? RoomShareWith { get; set; }
    public string? Notes { get; set; }

    // ----- display-only (set by the service; never bound) -----------------
    [BindNever] public ParticipantRole Role { get; set; }
    [BindNever] public bool IsLocked { get; set; }
    [BindNever] public string? Message { get; set; }

    /// <summary>REQUIREMENTS §51 — when this booking was last saved (UpdatedAt); null = never.</summary>
    [BindNever] public DateTimeOffset? LastSavedAt { get; set; }

    /// <summary>The booking confirmation number for the assigned hotel (REQUIREMENTS §46), read-only.</summary>
    [BindNever] public string? HotelConfirmationNumber { get; set; }

    /// <summary>The name of the hotel the participant is placed in (read-only confirmation block).</summary>
    [BindNever] public string? AssignedHotelName { get; set; }

    /// <summary>True once the assigned hotel carries a confirmation number — the reservation is CONFIRMED.</summary>
    [BindNever] public bool IsReservationConfirmed => !string.IsNullOrWhiteSpace(HotelConfirmationNumber);
}

/// <summary>
/// Shared submit-service for the Hotel form (REQUIREMENTS §148, REFERENCE step). It
/// encapsulates the form's ENTIRE behavior — the OnGet load, the OnPost
/// validate/persist, and ALL side-effects (auto-task ensure+done, late-change alert,
/// calendar invite) — so that BOTH the standalone <c>/Forms/Hotel</c> page and the inline
/// <see cref="HotelStepHandler"/> call the exact same logic and stay identical. Implements
/// the <see cref="IWizardFormService"/> marker so it self-registers by concrete type.
/// </summary>
public sealed class HotelFormService : IWizardFormService
{
    /// <summary>SourceKey prefix for the "complete the hotel form" auto-task — <c>hotel-form:{pid}</c>.</summary>
    public const string HotelTaskKey = "hotel-form";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly HotelCalendarInviter _inviter;
    private readonly OrganizerActionItemService _actions;
    private readonly IStringLocalizer<SharedResource> _loc;
    private readonly ILogger<HotelFormService> _logger;

    public HotelFormService(
        CommunityHubDbContext db,
        TimeProvider clock,
        HotelCalendarInviter inviter,
        OrganizerActionItemService actions,
        IStringLocalizer<SharedResource> loc,
        ILogger<HotelFormService> logger)
    {
        _db = db;
        _clock = clock;
        _inviter = inviter;
        _actions = actions;
        _loc = loc;
        _logger = logger;
    }

    /// <summary>The role-only relevance rule (excludes sponsors + attendees) — the historical gate.</summary>
    private static bool HotelRoleRelevant(ParticipantRole role) =>
        role is not (ParticipantRole.Sponsor or ParticipantRole.Attendee);

    /// <summary>
    /// FEATURE B eligibility (REQUIREMENTS §148 relevance gate): entitled to a hotel
    /// (<see cref="OrderItem.Hotel"/>), OR a non-speaker role that historically had the form.
    /// </summary>
    public async Task<bool> IsRelevantAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var entitled = await FormEntitlementGate.IsEntitledAsync(_db, eventId, participantId, OrderItem.Hotel, ct);
        var historicalNonSpeaker = role != ParticipantRole.Speaker && HotelRoleRelevant(role);
        return entitled || historicalNonSpeaker;
    }

    /// <summary>Completion detection (REQUIREMENTS §148) — a <see cref="HotelBooking"/> row exists.
    /// Mirrors SpeakerWizardService / RoleWizardService.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.HotelBookings.AnyAsync(h => h.EventId == eventId && h.ParticipantId == participantId, ct);

    /// <summary>
    /// Load the form's current state — the SAME load the standalone page's OnGet used:
    /// ensure the auto-task exists, hydrate from any existing booking, surface the lock
    /// state + the read-only reservation confirmation. Returns a fully-populated model.
    /// </summary>
    public async Task<HotelFormModel> LoadAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var model = new HotelFormModel { Role = role };

        model.IsLocked = await IsEditingLockedAsync(eventId, ct);
        await EnsureHotelTaskExistsAsync(eventId, participantId, ct);

        var existing = await _db.HotelBookings.FirstOrDefaultAsync(
            h => h.EventId == eventId && h.ParticipantId == participantId, ct);
        if (existing is not null)
        {
            model.NeedsRoom = existing.NeedsRoom;
            model.CheckInDate = existing.CheckInDate;
            model.CheckOutDate = existing.CheckOutDate;
            model.RoomShareWith = existing.RoomShareWith;
            model.Notes = existing.Notes;
            model.LastSavedAt = existing.UpdatedAt;
        }

        await PopulatePlacementAsync(model, eventId, participantId, ct);
        return model;
    }

    /// <summary>
    /// Validate + persist + run all side-effects (REQUIREMENTS §148) — the SAME logic the
    /// standalone page's OnPost ran. Field errors are written into <paramref name="modelState"/>
    /// (=> <see cref="WizardStepOutcome.Invalid"/>); on success the booking is upserted, the
    /// auto-task is marked done, a late-change alert is raised (edits only), and the calendar
    /// invite is (re)sent. The lock + relevance are RE-DERIVED from the DB here, so a crafted
    /// POST can never bypass them.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        HotelFormModel model, int eventId, int participantId, string email, string fullName,
        ParticipantRole role, ModelStateDictionary modelState, CancellationToken ct)
    {
        model.Role = role;

        // Relevance is re-checked server-side (never trusted from the post).
        if (!await IsRelevantAsync(eventId, participantId, role, ct))
            return WizardStepOutcome.NotRelevant;

        // Lock is re-derived server-side — read-only after the edition lock date.
        if (await IsEditingLockedAsync(eventId, ct))
        {
            model.IsLocked = true;
            model.Message = "Editing is closed for this event.";
            await PopulatePlacementAsync(model, eventId, participantId, ct);
            return WizardStepOutcome.Invalid;
        }

        // Field-level validation (REQUIREMENTS §21 shared validation pattern).
        if (model.NeedsRoom is null)
        {
            modelState.AddModelError(nameof(model.NeedsRoom), _loc["Hotel.ErrPickNeed"]);
        }
        else if (model.NeedsRoom == true)
        {
            if (model.CheckInDate is null)
                modelState.AddModelError(nameof(model.CheckInDate), _loc["Hotel.ErrCheckIn"]);
            if (model.CheckOutDate is null)
                modelState.AddModelError(nameof(model.CheckOutDate), _loc["Hotel.ErrCheckOut"]);
            if (model.CheckInDate is not null && model.CheckOutDate is not null && model.CheckOutDate <= model.CheckInDate)
                modelState.AddModelError(nameof(model.CheckOutDate), _loc["Hotel.ErrCheckOrder"]);
        }
        if (!modelState.IsValid)
        {
            // Re-render with field errors; nothing is persisted.
            await PopulatePlacementAsync(model, eventId, participantId, ct);
            return WizardStepOutcome.Invalid;
        }

        var booking = await _db.HotelBookings.FirstOrDefaultAsync(
            h => h.EventId == eventId && h.ParticipantId == participantId, ct);

        var isNewBooking = booking is null;
        if (booking is null)
        {
            booking = new HotelBooking
            {
                EventId = eventId,
                ParticipantId = participantId,
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
        booking.NeedsRoom = model.NeedsRoom!.Value;
        booking.CheckInDate = model.NeedsRoom == true ? model.CheckInDate : null;
        booking.CheckOutDate = model.NeedsRoom == true ? model.CheckOutDate : null;
        booking.RoomShareWith = model.NeedsRoom == true ? model.RoomShareWith : null;
        booking.Notes = model.Notes;

        await _db.SaveChangesAsync(ct);
        await MarkHotelTaskDoneAsync(eventId, participantId, ct);
        model.LastSavedAt = booking.UpdatedAt;
        model.Message = "Your hotel preference has been saved.";

        // Late-change alert: changing an ALREADY-submitted booking before the lock date
        // is something organizers must re-confirm with the hotel. First-time submissions
        // and early edits stay quiet.
        if (!isNewBooking)
        {
            var summary = booking.NeedsRoom
                ? $"Hotel changed to {booking.CheckInDate:dd MMM} → {booking.CheckOutDate:dd MMM}"
                  + (string.IsNullOrWhiteSpace(booking.RoomShareWith) ? "" : $", sharing with {booking.RoomShareWith}")
                : "Hotel changed to: no room needed";
            await _actions.RaiseIfLateAsync(
                eventId, OrganizerActionItemService.TypeHotelChanged, participantId, summary, ct);
        }

        // Send (or refresh) the calendar invite when real dates are picked. Stable UID per
        // (participant, event) means re-saves UPDATE the recipient's existing entry.
        if (booking.NeedsRoom && booking.CheckInDate is not null && booking.CheckOutDate is not null
            && booking.CheckInDate < booking.CheckOutDate)
        {
            try
            {
                var eventCode = await _db.Events
                    .Where(e => e.Id == eventId)
                    .Select(e => e.Code)
                    .FirstOrDefaultAsync(ct) ?? "Event Hub";

                var placement = await _db.Participants
                    .Where(p => p.Id == participantId && p.EventId == eventId)
                    .Select(p => new
                    {
                        HotelName = p.Hotel != null ? p.Hotel.Name : null,
                        HotelAddress = p.Hotel != null ? p.Hotel.Address : null,
                        p.HotelConfirmationNumber,
                    })
                    .FirstOrDefaultAsync(ct);

                await _inviter.SendAsync(
                    eventCode: eventCode,
                    toEmail: email,
                    fullName: fullName,
                    checkInDate: booking.CheckInDate.Value,
                    checkOutDate: booking.CheckOutDate.Value,
                    confirmed: booking.ConfirmationState == HotelConfirmationState.Confirmed,
                    confirmationNumber: booking.ConfirmationNumber,
                    roomType: booking.RoomType,
                    participantId: participantId,
                    eventId: eventId,
                    ct: ct,
                    hotelName: placement?.HotelName,
                    hotelAddress: placement?.HotelAddress,
                    hotelConfirmationNumber: placement?.HotelConfirmationNumber);

                booking.CalendarInviteSentAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                model.Message += " A calendar invitation has been emailed to you (marked [NOT CONFIRMED] until the hotel confirms).";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not send hotel calendar invite to {Email}", email);
                model.Message += $" (Calendar invite could not be sent: {ex.Message})";
            }
        }

        await PopulatePlacementAsync(model, eventId, participantId, ct);
        return WizardStepOutcome.Advance;
    }

    // ----- read-only reservation confirmation (REQUIREMENTS §46) ----------
    private async Task PopulatePlacementAsync(HotelFormModel model, int eventId, int participantId, CancellationToken ct)
    {
        var placement = await _db.Participants
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => new
            {
                HotelName = p.Hotel != null ? p.Hotel.Name : null,
                HotelLevelNumber = p.Hotel != null ? p.Hotel.ConfirmationNumber : null,
                PerPersonNumber = p.HotelConfirmationNumber,
            })
            .FirstOrDefaultAsync(ct);
        if (placement is not null)
        {
            model.AssignedHotelName = placement.HotelName;
            model.HotelConfirmationNumber = !string.IsNullOrWhiteSpace(placement.HotelLevelNumber)
                ? placement.HotelLevelNumber
                : placement.PerPersonNumber;
        }
    }

    // ----- auto-task: "Complete the Hotel form" --------------------------
    private async Task EnsureHotelTaskExistsAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{HotelTaskKey}:{participantId}";
        if (await _db.Tasks.AnyAsync(
                t => t.EventId == eventId && t.AssignedParticipantId == participantId
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

    private async Task MarkHotelTaskDoneAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{HotelTaskKey}:{participantId}";
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId
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
