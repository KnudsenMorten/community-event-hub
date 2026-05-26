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

    public bool IsLocked { get; private set; }
    public bool Saved { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        IsLocked = await IsEditingLockedAsync(me.EventId, ct);

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
        availability.MaxHoursPerDay = Math.Clamp(MaxHoursPerDay, 1, 24);

        await _db.SaveChangesAsync(ct);

        Saved = true;
        Step = 3;
        Message = "Thank you - your volunteer details are saved.";
        return Page();
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
