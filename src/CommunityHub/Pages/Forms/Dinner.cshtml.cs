using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using CommunityHub.Core.Resources;

namespace CommunityHub.Pages.Forms;

[Authorize]
public class DinnerModel : PageModel
{
    /// <summary>SourceKey prefix used for the "complete the dinner form" task.</summary>
    public const string DinnerTaskKey = "dinner-form";

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly IEmailSender _emailSender;
    private readonly EmailOptions _emailOptions;
    private readonly OrganizerActionItemService _actions;
    private readonly IStringLocalizer<SharedResource> _loc;

    public DinnerModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        IEmailSender emailSender,
        IOptions<EmailOptions> emailOptions,
        OrganizerActionItemService actions,
        IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _emailSender = emailSender;
        _emailOptions = emailOptions.Value;
        _actions = actions;
        _loc = loc;
    }

    [BindProperty] public DinnerRsvp Rsvp { get; set; } = DinnerRsvp.NotAnswered;
    [BindProperty] public int PlusOneCount { get; set; }
    [BindProperty] public string? AllergyNotes { get; set; }
    [BindProperty] public string? Comments { get; set; }

    /// <summary>Structured dietary/allergy capture (REQUIREMENTS §21) — shared with the Speaker form.</summary>
    [BindProperty] public CommunityHub.Pages.Shared.DietaryInput Dietary { get; set; } = new();

    public string FullName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;

    public string EventCode { get; private set; } = string.Empty;
    public string DinnerVenue { get; private set; } = "AC Hotel Bella Sky Copenhagen (speaker hotel)";
    public string DinnerDateLabel { get; private set; } = "9th Feb 2027";
    public string RsvpDeadlineLabel { get; private set; } = "Feb 1, 2027";

    public bool IsLocked { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await PopulateContextAsync(me.EventId, ct);
        FullName = me.FullName;
        Email = me.Email;

        IsLocked = await IsEditingLockedAsync(me.EventId, ct);
        await EnsureDinnerTaskExistsAsync(me.EventId, me.ParticipantId, ct);

        var existing = await _db.DinnerSignups.FirstOrDefaultAsync(
            d => d.EventId == me.EventId && d.ParticipantId == me.ParticipantId, ct);
        if (existing is not null)
        {
            Rsvp = existing.Rsvp;
            PlusOneCount = existing.PlusOneCount;
            AllergyNotes = existing.AllergyNotes;
            Comments = existing.Comments;
        }

        var diet = await _db.DietaryRequirements.FirstOrDefaultAsync(
            d => d.EventId == me.EventId && d.ParticipantId == me.ParticipantId
                 && d.Surface == DietarySurface.Dinner, ct);
        Dietary.LoadFrom(diet);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await PopulateContextAsync(me.EventId, ct);
        FullName = me.FullName;
        Email = me.Email;

        if (await IsEditingLockedAsync(me.EventId, ct))
        {
            IsLocked = true;
            Message = "Editing is closed for this event.";
            return Page();
        }

        // Field-level validation (REQUIREMENTS §21 shared validation pattern):
        // require an explicit pick: YES / NO / MAYBE -- not blank.
        if (Rsvp == DinnerRsvp.NotAnswered)
        {
            ModelState.AddModelError(nameof(Rsvp), _loc["Dinner.ErrPickRsvp"]);
        }
        if (!ModelState.IsValid)
        {
            // Re-render with field errors; nothing is persisted.
            return Page();
        }

        var signup = await _db.DinnerSignups.FirstOrDefaultAsync(
            d => d.EventId == me.EventId && d.ParticipantId == me.ParticipantId, ct);

        var isNew = signup is null;
        if (isNew)
        {
            signup = new DinnerSignup
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.DinnerSignups.Add(signup);
        }
        else
        {
            signup!.UpdatedAt = _clock.GetUtcNow();
        }

        var changedToYes = signup.Rsvp != DinnerRsvp.Yes && Rsvp == DinnerRsvp.Yes;

        signup.Rsvp = Rsvp;
        signup.PlusOneCount = Math.Max(0, PlusOneCount);
        signup.AllergyNotes = AllergyNotes;
        signup.Comments = Comments;
        // Keep legacy Attending in sync so older queries still work.
        signup.Attending = Rsvp == DinnerRsvp.Yes;
        signup.PlusOne = signup.PlusOneCount > 0;

        await SaveDietaryAsync(me.EventId, me.ParticipantId, ct);

        await _db.SaveChangesAsync(ct);
        await MarkDinnerTaskDoneAsync(me.EventId, me.ParticipantId, ct);
        Message = "Your RSVP has been saved.";

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
                me.EventId, OrganizerActionItemService.TypeDinnerChanged,
                me.ParticipantId, summary, ct);
        }

        // Auto-send calendar invitation when a participant becomes attending.
        if (Rsvp == DinnerRsvp.Yes)
        {
            try
            {
                await SendCalendarInviteAsync(signup, me.FullName, me.Email, ct);
                signup.CalendarInviteSentAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                Message += " A calendar invitation has been emailed to you.";
            }
            catch (Exception ex)
            {
                Message += $" (Calendar invitation could not be sent: {ex.Message})";
            }
        }

        return Page();
    }

    private async Task PopulateContextAsync(int eventId, CancellationToken ct)
    {
        var evt = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Code, e.VenueName })
            .FirstOrDefaultAsync(ct);
        if (evt is not null)
        {
            if (!string.IsNullOrWhiteSpace(evt.Code)) EventCode = evt.Code;
        }
    }

    // ----- Structured dietary capture (shared with the Speaker form) -----
    private async Task SaveDietaryAsync(int eventId, int participantId, CancellationToken ct)
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
        Dietary.ApplyTo(row);
    }

    // ----- Auto-task: "Complete the Dinner form" -------------------------
    private async Task EnsureDinnerTaskExistsAsync(
        int eventId, int participantId, CancellationToken ct)
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

    private async Task MarkDinnerTaskDoneAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{DinnerTaskKey}:{participantId}";
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null || task.State == TaskState.Done) return;
        task.State = TaskState.Done;
        await _db.SaveChangesAsync(ct);
    }

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
        DinnerSignup signup, string fullName, string toEmail, CancellationToken ct)
    {
        // 9 Feb 2027 18:00 CET = 17:00 UTC; ends 22:00 CET = 21:00 UTC (4h).
        var startUtc = new DateTimeOffset(2027, 2, 9, 17, 0, 0, TimeSpan.Zero);
        var endUtc   = startUtc.AddHours(4);

        var totalSeats = 1 + signup.PlusOneCount;
        var description =
            $"You are confirmed for the {EventCode} Appreciation Dinner.\n\n" +
            $"Seats: {totalSeats}\n" +
            $"Venue: {DinnerVenue}\n" +
            $"Time: 18:00 - 22:00 CET (the evening BEFORE the conference day).\n\n" +
            $"Allergies: {(string.IsNullOrWhiteSpace(signup.AllergyNotes) ? "(none)" : signup.AllergyNotes)}\n" +
            $"Comments: {(string.IsNullOrWhiteSpace(signup.Comments) ? "(none)" : signup.Comments)}\n\n" +
            $"See you there!\n\nCheers,\nELDK-team";

        var uid = $"dinner-{signup.EventId}-{signup.ParticipantId}@eventhub.expertslive.dk";
        var summary = $"{EventCode} Appreciation Dinner";

        var ics = IcsCalendarBuilder.BuildVEvent(
            uid: uid,
            summary: summary,
            description: description,
            location: DinnerVenue,
            startUtc: startUtc,
            endUtc: endUtc,
            organizerEmail: _emailOptions.FromAddress,
            organizerName: _emailOptions.FromDisplayName);

        var subject = summary;  // e.g. "ELDK27 Appreciation Dinner"
        var firstName = string.IsNullOrWhiteSpace(fullName) ? "there" : fullName.Split(' ')[0];
        var htmlBody =
            $"<p>Hi {System.Net.WebUtility.HtmlEncode(firstName)},</p>" +
            $"<p>Thank you for confirming your attendance at the {EventCode} Appreciation Dinner. " +
            $"Your calendar invitation is attached &mdash; open it to add the event to your calendar.</p>" +
            $"<p><strong>When:</strong> {DinnerDateLabel}, 18:00 / 6 pm CET<br/>" +
            $"<strong>Where:</strong> {DinnerVenue}<br/>" +
            $"<strong>Seats:</strong> {totalSeats}</p>" +
            $"<p>See you there!</p>" +
            $"<p>Cheers,<br/>ELDK-team</p>";

        await _emailSender.SendWithIcsAsync(
            toEmail, subject, htmlBody, ics, "dinner.ics", ct);
    }
}
