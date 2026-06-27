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
    [BindNever] public string DinnerVenue { get; set; } = "AC Hotel Bella Sky Copenhagen (speaker hotel)";
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
        IEmailContextAccessor? context = null)
    {
        _db = db;
        _clock = clock;
        _emailSender = emailSender;
        _emailOptions = emailOptions.Value;
        _actions = actions;
        _loc = loc;
        _context = context;
    }

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

        // Auto-send calendar invitation when a participant is attending.
        if (model.Rsvp == DinnerRsvp.Yes)
        {
            try
            {
                await SendCalendarInviteAsync(model, signup, fullName, email, ct);
                signup.CalendarInviteSentAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                model.Message += " A calendar invitation has been emailed to you.";
            }
            catch (Exception ex)
            {
                model.Message += $" (Calendar invitation could not be sent: {ex.Message})";
            }
        }

        return WizardStepOutcome.Advance;
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

    private async Task SendCalendarInviteAsync(
        DinnerFormModel model, DinnerSignup signup, string fullName, string toEmail, CancellationToken ct)
    {
        // 9 Feb 2027 18:00 CET = 17:00 UTC; ends 22:00 CET = 21:00 UTC (4h).
        var startUtc = new DateTimeOffset(2027, 2, 9, 17, 0, 0, TimeSpan.Zero);
        var endUtc   = startUtc.AddHours(4);

        var totalSeats = 1 + signup.PlusOneCount;
        var description =
            $"You are confirmed for the {model.EventCode} Appreciation Dinner.\n\n" +
            $"Seats: {totalSeats}\n" +
            $"Venue: {model.DinnerVenue}\n" +
            $"Time: 18:00 - 22:00 CET (the evening BEFORE the conference day).\n\n" +
            $"Allergies: {(string.IsNullOrWhiteSpace(signup.AllergyNotes) ? "(none)" : signup.AllergyNotes)}\n" +
            $"Comments: {(string.IsNullOrWhiteSpace(signup.Comments) ? "(none)" : signup.Comments)}\n\n" +
            $"See you there!\n\nCheers,\nELDK-team";

        var uid = $"dinner-{signup.EventId}-{signup.ParticipantId}@eventhub.expertslive.dk";
        var summary = $"{model.EventCode} Appreciation Dinner";

        var ics = IcsCalendarBuilder.BuildVEvent(
            uid: uid,
            summary: summary,
            description: description,
            location: model.DinnerVenue,
            startUtc: startUtc,
            endUtc: endUtc,
            organizerEmail: _emailOptions.FromAddress,
            organizerName: _emailOptions.FromDisplayName);

        var subject = summary;  // e.g. "ELDK27 Appreciation Dinner"
        var firstName = string.IsNullOrWhiteSpace(fullName) ? "there" : fullName.Split(' ')[0];
        var htmlBody =
            $"<p>Hi {System.Net.WebUtility.HtmlEncode(firstName)},</p>" +
            $"<p>Thank you for confirming your attendance at the {model.EventCode} Appreciation Dinner. " +
            $"Your calendar invitation is attached &mdash; open it to add the event to your calendar.</p>" +
            $"<p><strong>When:</strong> {model.DinnerDateLabel}, 18:00 / 6 pm CET<br/>" +
            $"<strong>Where:</strong> {model.DinnerVenue}<br/>" +
            $"<strong>Seats:</strong> {totalSeats}</p>" +
            $"<p>See you there!</p>" +
            $"<p>Cheers,<br/>ELDK-team</p>";

        // Ring-governed by the dinner-invite feature (operator 2026-06-22).
        using (_context?.Set(new EmailContext(
            "dinner-invite", signup.EventId, signup.ParticipantId, fullName, FeatureKey: "dinner-invite")))
        {
            await _emailSender.SendWithIcsAsync(
                toEmail, subject, htmlBody, ics, "dinner.ics", ct);
        }
    }
}
