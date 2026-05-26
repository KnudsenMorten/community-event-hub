using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Volunteer-availability form (CONTEXT.md section 9). One
/// VolunteerAvailability per participant per edition. The shift catalogue is
/// config-driven; the selected shifts are stored as a delimited string.
/// Read-only after the edition lock date.
/// </summary>
[Authorize]
public class VolunteerModel : PageModel
{
    private const char ShiftDelimiter = '|';

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public VolunteerModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    /// <summary>
    /// The shift catalogue. A documented default until the config system
    /// (Stage 6 wiring) supplies it from content.&lt;edition&gt;.json.
    /// </summary>
    public static readonly string[] ShiftCatalogue =
    {
        "Setup (day before)",
        "Registration desk - morning",
        "Registration desk - afternoon",
        "Session room support",
        "Sponsor / exhibitor area",
        "Teardown (after close)",
    };

    [BindProperty] public List<string> SelectedShifts { get; set; } = new();
    [BindProperty] public string? PreferredRole { get; set; }
    [BindProperty] public int MaxHoursPerDay { get; set; } = 8;

    public bool IsLocked { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        IsLocked = await IsEditingLockedAsync(me.EventId, ct);

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

        // Keep only catalogue values - guards against tampered form input.
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
        Message = "Your availability has been saved.";
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
