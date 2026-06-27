using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Volunteer sign-up as a multi-step wizard (CONTEXT.md section 9 - a wizard
/// is friendlier than one long form). Three steps:
///   1. Pick shifts
///   2. Preferred role + max hours
///   3. Review &amp; confirm -&gt; saves the VolunteerAvailability row
/// State is carried in hidden fields between steps (no server session needed),
/// so a refresh mid-wizard is harmless. Read-only after the edition lock date.
/// </summary>
[Authorize]
public class VolunteerWizardModel : PageModel
{
    /// <summary>SourceKey prefix for the "complete the volunteer sign-up" task.</summary>
    public const string VolunteerTaskKey = "volunteer-form";

    private const char ShiftDelimiter = '|';

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public VolunteerWizardModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    /// <summary>Same catalogue as the single-page form (config-driven later).</summary>
    public static readonly string[] ShiftCatalogue =
    {
        "Setup (day before)",
        "Registration desk - morning",
        "Registration desk - afternoon",
        "Session room support",
        "Sponsor / exhibitor area",
        "Teardown (after close)",
    };

    /// <summary>Current wizard step: 1, 2 or 3.</summary>
    [BindProperty] public int Step { get; set; } = 1;

    [BindProperty] public List<string> SelectedShifts { get; set; } = new();
    [BindProperty] public string? PreferredRole { get; set; }
    [BindProperty] public int MaxHoursPerDay { get; set; } = 8;

    /// <summary>Allowed range for <see cref="MaxHoursPerDay"/> (a single human's day).</summary>
    public const int MinHoursPerDay = 1;
    public const int MaxHoursPerDayLimit = 24;

    public bool IsLocked { get; private set; }
    public bool Saved { get; private set; }
    public string? Message { get; private set; }

    /// <summary>
    /// Set when the final-step POST is rejected by server-side validation
    /// (empty availability or out-of-range hours). Rendered on the review step
    /// so the volunteer sees WHY the submit did not go through and what the value
    /// they typed would have become — never silently clamped behind their back.
    /// </summary>
    public string? ValidationError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        IsLocked = await IsEditingLockedAsync(me.EventId, ct);
        await EnsureVolunteerTaskExistsAsync(me.EventId, me.ParticipantId, ct);

        // Pre-fill from an existing submission so the wizard edits it.
        var existing = await _db.VolunteerAvailabilities.FirstOrDefaultAsync(
            v => v.EventId == me.EventId && v.ParticipantId == me.ParticipantId,
            ct);
        if (existing is not null)
        {
            SelectedShifts = existing.SelectedShifts
                .Split(ShiftDelimiter, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            PreferredRole = existing.PreferredRole;
            MaxHoursPerDay = existing.MaxHoursPerDay;
        }
        Step = 1;
        return Page();
    }

    /// <summary>Move forward a step (Step is the step the user just completed).</summary>
    public IActionResult OnPostNext()
    {
        if (Step < 3) Step++;
        return Page();
    }

    /// <summary>Move back a step.</summary>
    public IActionResult OnPostBack()
    {
        if (Step > 1) Step--;
        return Page();
    }

    /// <summary>Final step: validate and save.</summary>
    public async Task<IActionResult> OnPostFinishAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        if (await IsEditingLockedAsync(me.EventId, ct))
        {
            IsLocked = true;
            Message = "Editing is closed for this event.";
            return Page();
        }

        // Keep only catalogue values - guards against tampered input.
        var clean = SelectedShifts
            .Where(s => ShiftCatalogue.Contains(s))
            .Distinct()
            .ToList();

        // --- Server-side validation (do NOT rely on the disabled button alone) ---
        // 1) An empty availability is meaningless: a volunteer who "signs up" for
        //    zero shifts has not signed up. Reject and keep them on the review step.
        if (clean.Count == 0)
        {
            Step = 3;
            ValidationError = "Pick at least one shift before you submit — you have none selected.";
            return Page();
        }

        // 2) Hours: non-numeric input fails int model-binding (MaxHoursPerDay stays
        //    the default) — surface that instead of silently saving the default.
        //    Out-of-range input was previously Math.Clamp'd with no feedback, so a
        //    volunteer who typed 40 was silently recorded as 24. Reject + tell them.
        if (!ModelState.IsValid && ModelState[nameof(MaxHoursPerDay)]?.Errors.Count > 0)
        {
            Step = 3;
            ValidationError = $"Enter a whole number of hours between {MinHoursPerDay} and {MaxHoursPerDayLimit}.";
            return Page();
        }
        if (MaxHoursPerDay < MinHoursPerDay || MaxHoursPerDay > MaxHoursPerDayLimit)
        {
            Step = 3;
            ValidationError = $"Max hours per day must be between {MinHoursPerDay} and {MaxHoursPerDayLimit} — you entered {MaxHoursPerDay}.";
            return Page();
        }

        var availability = await _db.VolunteerAvailabilities.FirstOrDefaultAsync(
            v => v.EventId == me.EventId && v.ParticipantId == me.ParticipantId,
            ct);

        if (availability is null)
        {
            availability = new VolunteerAvailability
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.VolunteerAvailabilities.Add(availability);
        }
        else
        {
            availability.UpdatedAt = _clock.GetUtcNow();
        }

        availability.SelectedShifts = string.Join(ShiftDelimiter, clean);
        availability.PreferredRole = PreferredRole;
        // Value is validated above (1..24), so what the volunteer saw on the review
        // step is exactly what we persist — no silent clamp behind their back.
        availability.MaxHoursPerDay = MaxHoursPerDay;

        await _db.SaveChangesAsync(ct);
        await MarkVolunteerTaskDoneAsync(me.EventId, me.ParticipantId, ct);

        Saved = true;
        Step = 3;
        Message = "Thank you - your volunteer details are saved.";
        return Page();
    }

    // ----- Auto-task: "Complete the Volunteer sign-up" -------------------
    private async Task EnsureVolunteerTaskExistsAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{VolunteerTaskKey}:{participantId}";
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
            Title = "Complete the Volunteer shifts sign-up",
            Description = "Pick the shifts you can cover and your preferred role. " +
                          "Finishing the wizard marks this task Done.",
            DueDate = due,
            State = TaskState.Open,
            SourceKey = sourceKey,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkVolunteerTaskDoneAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{VolunteerTaskKey}:{participantId}";
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null || task.State == TaskState.Done) return;
        task.State = TaskState.Done;
        task.CompletedAt = _clock.GetUtcNow();
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
}
