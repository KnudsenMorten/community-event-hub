using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer CRUD for the generic, role-tagged event SCHEDULE / key dates
/// (<see cref="ScheduleEntry"/>). Add / edit-by-delete / remove entries; each is
/// rendered role-filtered on the hub "Key dates" panel and synced to participants'
/// calendars (feed = sync-all; per-entry .ics = sync-individual). On first visit the
/// default ELDK schedule is seeded so there is something to edit.
/// </summary>
[Authorize]
public class ScheduleModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ScheduleService _schedule;
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public ScheduleModel(
        ICurrentParticipantAccessor participant,
        ScheduleService schedule,
        CommunityHubDbContext db,
        TimeProvider clock)
    {
        _participant = participant;
        _schedule = schedule;
        _db = db;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public List<ScheduleEntry> Entries { get; private set; } = new();
    public static string[] RoleKeywords => ScheduleRoles.Keywords;

    [BindProperty] public string? NewDate { get; set; }       // yyyy-MM-dd
    [BindProperty] public string? NewTime { get; set; }       // HH:mm (ignored when all-day)
    [BindProperty] public bool NewAllDay { get; set; }
    [BindProperty] public string? NewTitle { get; set; }
    [BindProperty] public string? NewLocation { get; set; }
    [BindProperty] public List<string> NewRoles { get; set; } = new();
    [BindProperty] public string? NewNotes { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await _schedule.EnsureSeededAsync(me.EventId, me.Email, ct);   // seed defaults on first visit
        Entries = await _schedule.GetAllAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!DateOnly.TryParse(NewDate, out var date) || string.IsNullOrWhiteSpace(NewTitle))
        {
            Message = "Enter at least a date and a title.";
            Entries = await _schedule.GetAllAsync(me.EventId, ct);
            return Page();
        }
        var time = (!NewAllDay && TimeOnly.TryParse(NewTime, out var t)) ? t : new TimeOnly(0, 0);
        var roles = NewRoles is { Count: > 0 } ? string.Join(",", NewRoles) : "all";

        _db.ScheduleEntries.Add(new ScheduleEntry
        {
            EventId = me.EventId,
            StartsAt = ScheduleService.EventLocal(date.ToDateTime(time)),
            AllDay = NewAllDay,
            Title = NewTitle.Trim(),
            Location = string.IsNullOrWhiteSpace(NewLocation) ? null : NewLocation.Trim(),
            Roles = roles,
            Notes = string.IsNullOrWhiteSpace(NewNotes) ? null : NewNotes.Trim(),
            CreatedAt = _clock.GetUtcNow(),
            LastUpdatedByEmail = me.Email,
        });
        await _db.SaveChangesAsync(ct);
        Message = "Schedule entry added.";
        Entries = await _schedule.GetAllAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var row = await _db.ScheduleEntries.FirstOrDefaultAsync(
            s => s.Id == id && s.EventId == me.EventId, ct);
        if (row is not null) { _db.ScheduleEntries.Remove(row); await _db.SaveChangesAsync(ct); Message = "Entry removed."; }
        Entries = await _schedule.GetAllAsync(me.EventId, ct);
        return Page();
    }
}
