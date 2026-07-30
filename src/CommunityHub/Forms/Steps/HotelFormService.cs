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

    /// <summary>
    /// §326q (operator 2026-07-25): the speaker's <see cref="Core.Domain.SpeakerCategory"/>
    /// (null for non-speakers / uncategorized). The hotel POLICY + extra-nights cost text
    /// are shown ONLY to Community speakers — a Guest speaker's hotel rides an individual
    /// agreement (§299 6.2), so the two-nights/one-night policy would be wrong for them;
    /// Sponsor speakers never see the form at all (no Hotel entitlement).
    /// </summary>
    [BindNever] public CommunityHub.Core.Domain.SpeakerCategory? SpeakerCategory { get; set; }

    /// <summary>REQUIREMENTS §51 — when this booking was last saved (UpdatedAt); null = never.</summary>
    [BindNever] public DateTimeOffset? LastSavedAt { get; set; }

    /// <summary>The booking confirmation number for the assigned hotel (REQUIREMENTS §46), read-only.</summary>
    [BindNever] public string? HotelConfirmationNumber { get; set; }

    /// <summary>The name of the hotel the participant is placed in (read-only confirmation block).</summary>
    [BindNever] public string? AssignedHotelName { get; set; }

    /// <summary>True once the assigned hotel carries a confirmation number — the reservation is CONFIRMED.</summary>
    [BindNever] public bool IsReservationConfirmed => !string.IsNullOrWhiteSpace(HotelConfirmationNumber);

    /// <summary>
    /// §403 (operator 2026-07-26: <i>"bug - i dont see year - can it default to feb 9, 2027 instead
    /// of taking the dates today"</i>). The earliest / latest night the event can plausibly need,
    /// derived from the edition's own dates — never hard-coded.
    ///
    /// <para>Rendered as <c>min</c>/<c>max</c> on the two date inputs. An empty
    /// <c>&lt;input type="date"&gt;</c> opens on TODAY, which is why he was looking at July 2026 for
    /// a February 2027 event; a <c>min</c> that lies in the future makes every browser open the
    /// picker THERE instead. That is the whole fix — no JavaScript, and it doubles as a guard
    /// against a typo'd year, which is the same bug from the other direction.</para>
    ///
    /// <para>Null when the edition config carries no dates: then the inputs render exactly as
    /// before rather than with a bound that might be wrong.</para>
    /// </summary>
    [BindNever] public DateOnly? StayWindowStart { get; set; }

    /// <inheritdoc cref="StayWindowStart"/>
    [BindNever] public DateOnly? StayWindowEnd { get; set; }
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
    private readonly OrganizerActionItemService _actions;
    private readonly IStringLocalizer<SharedResource> _loc;
    private readonly ILogger<HotelFormService> _logger;

    // §512 — this service no longer takes a HotelCalendarInviter. The submit-time auto-invite it
    // was for is RETIRED: the participant now presses "Add to calendar" (SendItemInviteAsync
    // below, reusing the same UID). The inviter was still being injected and assigned to a field
    // that nothing read, which is what made the feature look orphaned on the Settings page.
    // The inviter itself is still live — the ORGANIZER's confirmation re-issue calls it.
    public HotelFormService(
        CommunityHubDbContext db,
        TimeProvider clock,
        OrganizerActionItemService actions,
        IStringLocalizer<SharedResource> loc,
        ILogger<HotelFormService> logger,
        // §322n: optional + last (existing unit tests need not construct it; DI injects).
        CommunityHub.Core.Email.CalendarInviteEmailService? calendarInvite = null,
        // §403: same optional-DI pattern — the edition's own dates drive the picker's min/max.
        CommunityHub.Core.Config.EventEditionConfigLoader? configLoader = null,
        CommunityHub.Core.Config.EventConfigOptions? configOptions = null)
    {
        _db = db;
        _clock = clock;
        _actions = actions;
        _loc = loc;
        _logger = logger;
        _calendarInvite = calendarInvite;
        (_stayWindowStart, _stayWindowEnd) = ResolveStayWindow(configLoader, configOptions);
        (_defaultCheckIn, _defaultCheckOut) = ResolveDefaultStay(configLoader, configOptions);
    }

    private readonly CommunityHub.Core.Email.CalendarInviteEmailService? _calendarInvite;
    private readonly DateOnly? _stayWindowStart;
    private readonly DateOnly? _stayWindowEnd;
    private readonly DateOnly? _defaultCheckIn;
    private readonly DateOnly? _defaultCheckOut;

    /// <summary>
    /// §403 — the bookable window, derived from the edition's own <c>dates</c> block: from two
    /// nights BEFORE the pre-day to two nights AFTER the last conference day.
    ///
    /// <para>The padding is deliberate and generous. A speaker flying in from further away arrives
    /// the night before setup, and someone on a Sunday flight leaves after day 2 — bounding tightly
    /// to the three event days would BLOCK a legitimate booking, which is a far worse failure than
    /// a picker that opens on the wrong month. The bound exists to put the calendar in the right
    /// place, not to police the stay.</para>
    ///
    /// <para>Fail-soft in every direction: no loader, no config file, no dates block, or an
    /// unparseable date all yield <c>(null, null)</c> and the inputs render exactly as before.</para>
    /// </summary>
    private static (DateOnly? Start, DateOnly? End) ResolveStayWindow(
        CommunityHub.Core.Config.EventEditionConfigLoader? loader,
        CommunityHub.Core.Config.EventConfigOptions? options)
    {
        if (loader is null) return (null, null);

        try
        {
            return StayWindowFor(loader
                .Load((options ?? new CommunityHub.Core.Config.EventConfigOptions()).EventConfigPath)
                .Dates);
        }
        catch
        {
            // A hotel form must never fail to render because a config file is malformed.
            return (null, null);
        }
    }

    /// <summary>§425 — the pre-filled dates, loaded with the same fail-soft contract as the bounds.</summary>
    private static (DateOnly? CheckIn, DateOnly? CheckOut) ResolveDefaultStay(
        CommunityHub.Core.Config.EventEditionConfigLoader? loader,
        CommunityHub.Core.Config.EventConfigOptions? options)
    {
        if (loader is null) return (null, null);

        try
        {
            return DefaultStayFor(loader
                .Load((options ?? new CommunityHub.Core.Config.EventConfigOptions()).EventConfigPath)
                .Dates);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// §403 — the pure derivation, exposed so it can be pinned directly: edition dates in, picker
    /// bounds out. Null / blank / unparseable in any combination yields <c>(null, null)</c> rather
    /// than a guess, because a WRONG bound would block a real booking.
    /// </summary>
    public static (DateOnly? Start, DateOnly? End) StayWindowFor(
        CommunityHub.Core.Config.EditionDates? dates)
    {
        if (dates is null) return (null, null);

        // preDay is the earliest event day; day2 falls back to day1 for a one-day edition.
        if (!DateOnly.TryParse(dates.PreDay, out var first)) return (null, null);
        if (!DateOnly.TryParse(dates.Day2, out var last)
            && !DateOnly.TryParse(dates.Day1, out last)) last = first;

        return (first.AddDays(-2), last.AddDays(2));
    }

    /// <summary>
    /// §425 — the PRE-FILLED dates for someone who has not booked yet: <c>day1 → day2</c>.
    ///
    /// <para><b>Why this exists on top of §403's bounds.</b> §403 set <c>min</c>/<c>max</c> on the
    /// inputs expecting browsers to open the picker at <c>min</c>. They do not — Chrome opens an
    /// EMPTY date input on the current month whatever the bounds say, so the operator was still
    /// staring at July 2026 while booking a February 2027 event and reported it a second time
    /// (2026-07-27: <i>"hotel calendar is still missing year + it should default to 9th feb 2027 -
    /// i asked for this yesterday as well"</i>). A real <c>value</c> is the only thing that moves
    /// the picker, so the field now carries one.</para>
    ///
    /// <para>Deliberately <b>day1</b>, not <c>preDay</c>: preDay is the master-class / setup day,
    /// and the one-night main-day stay is what most people book. Master-class speakers who get the
    /// extra night change one field; everyone else gets the dates they would have typed. The
    /// bounds from §403 still allow the whole window either side.</para>
    ///
    /// <para>Same fail-soft contract as <see cref="StayWindowFor"/>: unparseable config yields
    /// <c>(null, null)</c> and the inputs render empty, exactly as before.</para>
    /// </summary>
    public static (DateOnly? CheckIn, DateOnly? CheckOut) DefaultStayFor(
        CommunityHub.Core.Config.EditionDates? dates)
    {
        if (dates is null) return (null, null);

        // day1 is the main conference day; fall back to preDay when an edition has no day1.
        if (!DateOnly.TryParse(dates.Day1, out var checkIn)
            && !DateOnly.TryParse(dates.PreDay, out checkIn)) return (null, null);

        // Check-out is the morning after the last day the person is here for. day2 when the
        // edition has one, otherwise the night after check-in.
        if (!DateOnly.TryParse(dates.Day2, out var checkOut) || checkOut <= checkIn)
            checkOut = checkIn.AddDays(1);

        return (checkIn, checkOut);
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

    /// <summary>§403 — stamp the picker bounds onto the model (BindNever, so never from the POST).</summary>
    private void ApplyStayWindow(HotelFormModel model)
    {
        model.StayWindowStart = _stayWindowStart;
        model.StayWindowEnd = _stayWindowEnd;
    }

    /// <summary>§326q: load the speaker's category for the view's Community-only policy gate.</summary>
    private async Task PopulateSpeakerCategoryAsync(
        HotelFormModel model, int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        if (role != ParticipantRole.Speaker) return;
        model.SpeakerCategory = await _db.SpeakerProfiles
            .Where(s => s.EventId == eventId && s.ParticipantId == participantId)
            .Select(s => s.Category)
            .FirstOrDefaultAsync(ct);
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
        await PopulateSpeakerCategoryAsync(model, eventId, participantId, role, ct);

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

        // §425 — pre-fill the dates for someone who has not chosen any yet, so the picker opens
        // on the EVENT's month instead of today's. Only ever fills a blank: a saved booking above
        // has already set these, and this must never quietly move a date a person chose.
        model.CheckInDate ??= _defaultCheckIn;
        model.CheckOutDate ??= _defaultCheckOut;

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
        // §326q: the re-rendered form (invalid post / lock) needs the category too.
        await PopulateSpeakerCategoryAsync(model, eventId, participantId, role, ct);

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

        // §322n (operator 2026-07-24): the AUTO e-mail on save is GONE ("drop these 2
        // auto-emails") — it had degraded to a links-only mail (§257 auto-invite off).
        // The participant now clicks "Email me a calendar invite" on the form instead,
        // which sends a REGULAR calendar invitation (SendInviteEmailAsync below).

        await PopulatePlacementAsync(model, eventId, participantId, ct);
        return WizardStepOutcome.Advance;
    }

    /// <summary>
    /// §322n: USER-INITIATED hotel calendar invite — the standard §193 invitation
    /// (CalendarInviteEmailService: honors the calendar/override e-mail, stable UID so a
    /// re-send UPDATES the existing entry). Requires a saved booking with real dates.
    /// </summary>
    public async Task<(bool Ok, string Message)> SendInviteEmailAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        if (_calendarInvite is null)
            return (false, "Calendar invitations aren't available right now.");

        var booking = await _db.HotelBookings.AsNoTracking().FirstOrDefaultAsync(
            b => b.EventId == eventId && b.ParticipantId == participantId, ct);
        if (booking is null || !booking.NeedsRoom
            || booking.CheckInDate is null || booking.CheckOutDate is null
            || booking.CheckInDate >= booking.CheckOutDate)
        {
            return (false, "Save your hotel dates first — then the invite has something to contain.");
        }

        var eventCode = await _db.Events.Where(e => e.Id == eventId)
            .Select(e => e.Code).FirstOrDefaultAsync(ct) ?? "Event Hub";
        var placement = await _db.Participants
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => new
            {
                HotelName = p.Hotel != null ? p.Hotel.Name : null,
                HotelAddress = p.Hotel != null ? p.Hotel.Address : null,
                p.HotelConfirmationNumber,
            })
            .FirstOrDefaultAsync(ct);

        var startUtc = new DateTimeOffset(booking.CheckInDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endUtc = new DateTimeOffset(booking.CheckOutDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1);
        // §434 (operator 2026-07-27: "this calendar invite must have different subject to
        // 'ELDK27 Hotel Reservation' - and then it must include address to hotel once it is ready
        // and assigned to person", then "it just says 'your assigned hotel' but it should state
        // something different when its not assigned yet").
        //
        // The old label was the SAME string in both states: an unassigned booking produced the
        // subject "ELDK27 Hotel — your assigned hotel" and a LOCATION reading "your assigned
        // hotel". In a calendar that is not a placeholder, it is a claim — the entry looks like it
        // names a hotel, and the location field looks filled in, so there is nothing to tell you
        // the organizers have not picked one yet.
        var isAssigned = !string.IsNullOrWhiteSpace(placement?.HotelName);

        // Subject: the reservation FIRST, so it is recognisable in a calendar list either way; the
        // hotel name is appended only once there genuinely is one.
        var summary = isAssigned
            ? $"{eventCode} Hotel Reservation — {placement!.HotelName}"
            : $"{eventCode} Hotel Reservation";

        // Location: the full address once assigned (this is the field a phone routes you from), and
        // an explicit not-yet otherwise rather than a phrase that reads like a name.
        var location = isAssigned
            ? (string.IsNullOrWhiteSpace(placement!.HotelAddress)
                ? placement.HotelName!
                : $"{placement.HotelName}, {placement.HotelAddress}")
            : "Hotel not assigned yet — the organizers will update this invitation";

        var placementLine = isAssigned
            ? $"Hotel: {placement!.HotelName}"
              + (string.IsNullOrWhiteSpace(placement.HotelAddress) ? "" : $"\nAddress: {placement.HotelAddress}")
            : "Your hotel has not been assigned yet. The organizers will assign one, and this "
              + "calendar entry updates itself with the hotel name and address — you do not need "
              + "to do anything.";

        var confirmation = string.IsNullOrWhiteSpace(placement?.HotelConfirmationNumber)
            ? "Awaiting hotel confirmation — this entry is updated once the hotel returns the confirmation number."
            : $"Hotel confirmation number: {placement!.HotelConfirmationNumber}";

        // The SAME UID the retired auto-mail used — a button re-send UPDATES that entry. This is
        // what makes the "it updates itself" promise above true: when the organizers assign a
        // hotel, the re-send lands on the same calendar entry rather than creating a second one.
        var sent = await _calendarInvite.SendItemInviteAsync(
            participantId,
            uid: $"hotel-{eventId}-{participantId}@eventhub.expertslive.dk",
            summary: summary,
            description: $"Your hotel reservation for {eventCode}.\n"
                + $"Check-in: {booking.CheckInDate:d MMM yyyy} — check-out: {booking.CheckOutDate:d MMM yyyy}.\n"
                + placementLine + "\n"
                + confirmation,
            location: location,
            start: startUtc,
            end: endUtc,
            allDay: true,
            fileName: "hotel.ics",
            introHtml: $"Here is your calendar invitation for your {System.Net.WebUtility.HtmlEncode(eventCode)} hotel stay.",
            ct: ct,
            // §705.15 — the guest pressed "Add to calendar" on their OWN hotel form: user-initiated, so
            // exempt. Distinct from hotel-confirmation-guest, the organizer-triggered mail to every guest
            // in a hotel, which keeps a ring.
            mailKey: "hotel-calendar-selfsend");
        return sent
            ? (true, sent.Confirmation())
            : (false, "Calendar invitations are turned off for this event.");
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
