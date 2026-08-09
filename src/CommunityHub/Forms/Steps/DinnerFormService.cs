using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Resources;
using CommunityHub.Pages.Shared;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Dinner (Appreciation Dinner RSVP) step
/// (REQUIREMENTS §148). It is shared by the standalone <c>/Forms/Dinner</c> page AND the
/// inline wizard step, and is the model the <c>_DinnerFields</c> partial binds to. The
/// EDITABLE fields (top of the class) are the only ones model binding fills; the DISPLAY
/// fields are <see cref="BindNeverAttribute"/> and are populated by
/// <see cref="DinnerFormService"/> (load + save), never from the POST.
/// </summary>
public sealed class DinnerFormModel
{
    // ----- editable (bound from the POST) --------------------------------
    public DinnerRsvp Rsvp { get; set; } = DinnerRsvp.NotAnswered;
    public int PlusOneCount { get; set; }
    public string? Comments { get; set; }

    /// <summary>Structured dietary/allergy capture (REQUIREMENTS §21) — shared with the Speaker form.</summary>
    public DietaryInput Dietary { get; set; } = new();

    // ----- display-only (set by the service; never bound) -----------------
    [BindNever] public ParticipantRole Role { get; set; }
    [BindNever] public bool IsLocked { get; set; }
    [BindNever] public string? Message { get; set; }

    /// <summary>REQUIREMENTS §51 — when this dinner RSVP was last saved (UpdatedAt); null = never saved.</summary>
    [BindNever] public DateTimeOffset? LastSavedAt { get; set; }

    [BindNever] public string FullName { get; set; } = string.Empty;
    [BindNever] public string Email { get; set; } = string.Empty;

    [BindNever] public string EventCode { get; set; } = string.Empty;
    // §978 (operator 2026-08-09): *"remeve the word here (speaker hotel) from the appreciation
    // dinner form"*. The dinner invites volunteers, media, event partners, sponsors, VIPs and
    // organizers as well as speakers — calling the venue "the speaker hotel" reads, to most of the
    // people receiving it, as somewhere they are not staying.
    [BindNever] public string DinnerVenue { get; set; } = "AC Hotel Bella Sky Copenhagen";
    [BindNever] public string DinnerDateLabel { get; set; } = "9th Feb 2027";
    [BindNever] public string RsvpDeadlineLabel { get; set; } = "Feb 1, 2027";
}

/// <summary>
/// Shared submit-service for the Dinner form (REQUIREMENTS §148). It encapsulates the
/// form's ENTIRE behavior — the OnGet load, the OnPost validate/persist, and ALL
/// side-effects (structured dietary upsert, auto-task ensure+done, late-change alert,
/// ICS calendar invite on RSVP=Yes) — so that BOTH the standalone <c>/Forms/Dinner</c>
/// page and the inline <see cref="DinnerStepHandler"/> call the exact same logic and stay
/// byte-for-byte identical. Implements the <see cref="IWizardFormService"/> marker so it
/// self-registers by concrete type.
/// </summary>
public sealed class DinnerFormService : IWizardFormService
{
    /// <summary>SourceKey prefix for the "complete the dinner form" auto-task — <c>dinner-form:{pid}</c>.</summary>
    public const string DinnerTaskKey = "dinner-form";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IEmailSender _emailSender;
    private readonly EmailOptions _emailOptions;
    private readonly OrganizerActionItemService _actions;
    private readonly IStringLocalizer<SharedResource> _loc;
    private readonly IEmailContextAccessor? _context;

    public DinnerFormService(
        CommunityHubDbContext db,
        TimeProvider clock,
        IEmailSender emailSender,
        IOptions<EmailOptions> emailOptions,
        OrganizerActionItemService actions,
        IStringLocalizer<SharedResource> loc,
        IEmailContextAccessor? context = null,
        // §322n: optional + last (existing unit tests need not construct it; DI injects).
        CommunityHub.Core.Email.CalendarInviteEmailService? calendarInvite = null)
    {
        _db = db;
        _clock = clock;
        _emailSender = emailSender;
        _emailOptions = emailOptions.Value;
        _actions = actions;
        _loc = loc;
        _context = context;
        _calendarInvite = calendarInvite;
    }

    private readonly CommunityHub.Core.Email.CalendarInviteEmailService? _calendarInvite;

    /// <summary>
    /// FEATURE B eligibility (REQUIREMENTS §148 relevance gate): the appreciation-dinner
    /// RSVP is gated by ENTITLEMENT (<see cref="OrderItem.AppreciationDinner"/>) for speakers
    /// — a self-funded speaker IS entitled and still sees the form; a speaker with no dinner
    /// entitlement is denied. Every NON-speaker role keeps its prior access (the form
    /// historically had no role gate), so access is never silently removed.
    /// </summary>
    public async Task<bool> IsRelevantAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        if (role != ParticipantRole.Speaker) return true; // historical: every non-speaker role had the form
        return await FormEntitlementGate.IsEntitledAsync(
            _db, eventId, participantId, OrderItem.AppreciationDinner, ct);
    }

    /// <summary>Completion detection (REQUIREMENTS §148) — a <see cref="DinnerSignup"/> row exists.
    /// Mirrors SpeakerWizardService / RoleWizardService.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.DinnerSignups.AnyAsync(d => d.EventId == eventId && d.ParticipantId == participantId, ct);

    /// <summary>
    /// Load the form's current state — the SAME load the standalone page's OnGet used:
    /// hydrate the event context, surface the lock state, ensure the auto-task exists,
    /// and hydrate from any existing signup + dietary row. Returns a fully-populated model.
    /// </summary>
    public async Task<DinnerFormModel> LoadAsync(
        int eventId, int participantId, ParticipantRole role, string email, string fullName, CancellationToken ct)
    {
        var model = new DinnerFormModel { Role = role, FullName = fullName, Email = email };
        await PopulateContextAsync(model, eventId, ct);

        model.IsLocked = await IsEditingLockedAsync(eventId, ct);
        await EnsureDinnerTaskExistsAsync(eventId, participantId, ct);

        var existing = await _db.DinnerSignups.FirstOrDefaultAsync(
            d => d.EventId == eventId && d.ParticipantId == participantId, ct);
        if (existing is not null)
        {
            model.Rsvp = existing.Rsvp;
            model.PlusOneCount = existing.PlusOneCount;
            model.Comments = existing.Comments;
            model.LastSavedAt = existing.UpdatedAt;
        }

        var diet = await _db.DietaryRequirements.FirstOrDefaultAsync(
            d => d.EventId == eventId && d.ParticipantId == participantId
                 && d.Surface == DietarySurface.Dinner, ct);
        model.Dietary.LoadFrom(diet);
        return model;
    }

    /// <summary>
    /// Validate + persist + run all side-effects (REQUIREMENTS §148) — the SAME logic the
    /// standalone page's OnPost ran. Field errors are written into <paramref name="modelState"/>
    /// (=> <see cref="WizardStepOutcome.Invalid"/>); on success the signup is upserted, the
    /// structured dietary row is saved, the auto-task is marked done, a late-change alert is
    /// raised (edits only), and on RSVP=Yes the ICS calendar invite is sent. The lock +
    /// relevance are RE-DERIVED from the DB here, so a crafted POST can never bypass them.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        DinnerFormModel model, int eventId, int participantId, string email, string fullName,
        ParticipantRole role, ModelStateDictionary modelState, CancellationToken ct)
    {
        model.Role = role;
        model.FullName = fullName;
        model.Email = email;
        await PopulateContextAsync(model, eventId, ct);

        // Relevance is re-checked server-side (never trusted from the post).
        if (!await IsRelevantAsync(eventId, participantId, role, ct))
            return WizardStepOutcome.NotRelevant;

        // Lock is re-derived server-side — read-only after the edition lock date.
        if (await IsEditingLockedAsync(eventId, ct))
        {
            model.IsLocked = true;
            model.Message = "Editing is closed for this event.";
            return WizardStepOutcome.Invalid;
        }

        // Field-level validation (REQUIREMENTS §21 shared validation pattern):
        // require an explicit pick: YES / NO / MAYBE -- not blank.
        if (model.Rsvp == DinnerRsvp.NotAnswered)
        {
            modelState.AddModelError(nameof(model.Rsvp), _loc["Dinner.ErrPickRsvp"]);
        }
        if (!modelState.IsValid)
        {
            // Re-render with field errors; nothing is persisted.
            return WizardStepOutcome.Invalid;
        }

        var signup = await _db.DinnerSignups.FirstOrDefaultAsync(
            d => d.EventId == eventId && d.ParticipantId == participantId, ct);

        var isNew = signup is null;
        if (isNew)
        {
            signup = new DinnerSignup
            {
                EventId = eventId,
                ParticipantId = participantId,
                CreatedAt = _clock.GetUtcNow(),
                UpdatedAt = _clock.GetUtcNow(),
            };
            _db.DinnerSignups.Add(signup);
        }
        else
        {
            signup!.UpdatedAt = _clock.GetUtcNow();
        }

        signup.Rsvp = model.Rsvp;
        signup.PlusOneCount = Math.Max(0, model.PlusOneCount);
        // AllergyNotes field removed from the form (redundant with the dietary
        // partial's "Other allergies"); don't overwrite any existing value on save.
        signup.Comments = model.Comments;
        // Keep legacy Attending/PlusOne in sync so older queries still work.
        signup.Attending = model.Rsvp == DinnerRsvp.Yes;
        signup.PlusOne = signup.PlusOneCount > 0;

        await SaveDietaryAsync(model, eventId, participantId, ct);

        await _db.SaveChangesAsync(ct);
        await MarkDinnerTaskDoneAsync(eventId, participantId, ct);
        model.LastSavedAt = signup.UpdatedAt;
        model.Message = "Your RSVP has been saved.";

        // Late-change alert: changing an ALREADY-submitted RSVP inside the window
        // before the lock date affects the caterer's head-count + allergy list.
        if (!isNew)
        {
            var allergyNote = string.IsNullOrWhiteSpace(signup.AllergyNotes)
                ? "" : ", allergies updated";
            var summary = $"Dinner RSVP changed to {signup.Rsvp}" +
                          (signup.PlusOneCount > 0 ? $" (+{signup.PlusOneCount})" : "") +
                          allergyNote;
            await _actions.RaiseIfLateAsync(
                eventId, OrganizerActionItemService.TypeDinnerChanged,
                participantId, summary, ct);
        }

        // §322n (operator 2026-07-24): the AUTO e-mail on RSVP=Yes is GONE ("drop these 2
        // auto-emails") — it had degraded to a links-only mail (§257 auto-invite off).
        // The participant now clicks "Email me a calendar invite" on the form instead
        // (SendInviteEmailAsync below sends the REGULAR invitation).

        return WizardStepOutcome.Advance;
    }

    /// <summary>
    /// §322n: USER-INITIATED dinner calendar invite — the standard §193 invitation
    /// (CalendarInviteEmailService: honors the calendar/override e-mail, stable UID so a
    /// re-send UPDATES the existing entry). Requires a saved RSVP = Yes.
    /// </summary>
    public async Task<(bool Ok, string Message)> SendInviteEmailAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        if (_calendarInvite is null)
            return (false, "Calendar invitations aren't available right now.");

        var signup = await _db.DinnerSignups.AsNoTracking().FirstOrDefaultAsync(
            d => d.EventId == eventId && d.ParticipantId == participantId, ct);
        if (signup is null || !signup.Attending)
        {
            return (false, "RSVP Yes and save first — then the invite has something to contain.");
        }

        var eventCode = await _db.Events.Where(e => e.Id == eventId)
            .Select(e => e.Code).FirstOrDefaultAsync(ct) ?? "Event Hub";
        var model = new DinnerFormModel();   // carries the venue/date labels

        // §298: dinner 18:30–22:30 CET on 9 Feb 2027 (17:30–21:30 UTC).
        var startUtc = new DateTimeOffset(2027, 2, 9, 17, 30, 0, TimeSpan.Zero);
        var endUtc = startUtc.AddHours(4);
        var totalSeats = 1 + signup.PlusOneCount;

        // The SAME UID the retired auto-mail used — a button re-send UPDATES that entry.
        var sent = await _calendarInvite.SendItemInviteAsync(
            participantId,
            uid: $"dinner-{eventId}-{participantId}@eventhub.expertslive.dk",
            summary: $"{eventCode} Appreciation Dinner",
            description: $"You are confirmed for the {eventCode} Appreciation Dinner.\n"
                + $"Seats: {totalSeats}\n"
                + $"Venue: {model.DinnerVenue}\n"
                + "Time: 18:30 - 22:30 CET (the evening BEFORE the conference day).",
            location: model.DinnerVenue,
            start: startUtc,
            end: endUtc,
            allDay: false,
            fileName: "dinner.ics",
            introHtml: $"Here is your calendar invitation for the {System.Net.WebUtility.HtmlEncode(eventCode)} Appreciation Dinner.",
            ct: ct,
            // §705.15a — name the mail so it stops sharing one Settings row with the activation and
            // hotel calendar invites. Still user-initiated, so still ring-exempt.
            mailKey: "calendar-dinner");
        return sent
            ? (true, sent.Confirmation())
            : (false, "Calendar invitations are turned off for this event.");
    }

    private async Task PopulateContextAsync(DinnerFormModel model, int eventId, CancellationToken ct)
    {
        var evt = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Code })
            .FirstOrDefaultAsync(ct);
        if (evt is not null && !string.IsNullOrWhiteSpace(evt.Code)) model.EventCode = evt.Code;
    }

    // ----- Structured dietary capture (shared with the Speaker form) -----
    private async Task SaveDietaryAsync(DinnerFormModel model, int eventId, int participantId, CancellationToken ct)
    {
        var row = await _db.DietaryRequirements.FirstOrDefaultAsync(
            d => d.EventId == eventId && d.ParticipantId == participantId
                 && d.Surface == DietarySurface.Dinner, ct);
        if (row is null)
        {
            row = new DietaryRequirement
            {
                EventId = eventId,
                ParticipantId = participantId,
                Surface = DietarySurface.Dinner,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.DietaryRequirements.Add(row);
        }
        else
        {
            row.UpdatedAt = _clock.GetUtcNow();
        }
        model.Dietary.ApplyTo(row);
    }

    // ----- Auto-task: "Complete the Dinner form" -------------------------
    private async Task EnsureDinnerTaskExistsAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{DinnerTaskKey}:{participantId}";
        if (await _db.Tasks.AnyAsync(
                t => t.EventId == eventId
                     && t.AssignedParticipantId == participantId
                     && t.SourceKey == sourceKey, ct)) return;

        var due = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (DateOnly?)e.StartDate.AddDays(-21))
            .FirstOrDefaultAsync(ct);

        _db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId,
            AssignedParticipantId = participantId,
            Title = "Complete the Appreciation Dinner RSVP",
            Description = "RSVP yes/no (+ plus-one count + allergies). Saving the form marks this task Done.",
            DueDate = due,
            State = TaskState.Open,
            SourceKey = sourceKey,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkDinnerTaskDoneAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{DinnerTaskKey}:{participantId}";
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

    // (§322n: the old auto-send mail builder is gone — the user-initiated button uses
    // CalendarInviteEmailService.SendItemInviteAsync, the standard §193 invitation.)
}
