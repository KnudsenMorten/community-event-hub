using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1380 — the volunteer TASK CALENDAR: a Sessionize-schedule-builder style grid per day, one column per bucket,
/// time running down; click a task for its notes and who is on it. Leth 2026-11-01: <i>"Overblik over tasks i et
/// kalender view … gå ind på den specifikke task, og se alle de ting der er noteret omkring tasken, samt hvem der
/// er assigned"</i>. v1 is READ-ONLY (editing stays on Work structure and the task editor). Organizer-only.
/// </summary>
[Authorize]
public class VolunteerCalendarModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;

    public VolunteerCalendarModel(ICurrentParticipantAccessor participant, CommunityHubDbContext db)
    {
        _participant = participant;
        _db = db;
    }

    public bool AccessDenied { get; private set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Day { get; set; }

    public IReadOnlyList<CalendarDay> Days { get; private set; } = Array.Empty<CalendarDay>();
    public CalendarDay? Current { get; private set; }
    public Dictionary<int, string> ColumnNames { get; } = new();
    public Dictionary<int, VolunteerTask> Tasks { get; } = new();
    public Dictionary<int, List<string>> Published { get; } = new();
    public Dictionary<int, List<string>> Queued { get; } = new();
    public int UndatedCount { get; private set; }

    /// <summary>Pixels per minute in the grid — 1.2 makes an hour 72px, readable on a phone.</summary>
    public const double PxPerMinute = 1.2;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) { AccessDenied = true; return Page(); }

        var eventId = me.EventId;
        foreach (var c in await _db.VolunteerCategories.Where(c => c.EventId == eventId).ToListAsync(ct))
            ColumnNames[c.Id] = c.Name;
        ColumnNames[0] = "(no bucket)";

        var tasks = await _db.VolunteerTasks.Where(t => t.EventId == eventId)
            .Include(t => t.Subcategory).ToListAsync(ct);
        foreach (var t in tasks) Tasks[t.Id] = t;
        UndatedCount = tasks.Count(t => t.DueDate is null);

        var names = await _db.Participants.Where(p => p.EventId == eventId)
            .Select(p => new { p.Id, p.FullName }).ToDictionaryAsync(p => p.Id, p => p.FullName, ct);
        foreach (var a in await _db.VolunteerTaskAssignments.Where(a => a.EventId == eventId)
                     .Select(a => new { a.TaskId, a.ParticipantId }).ToListAsync(ct))
            (Published.TryGetValue(a.TaskId, out var l) ? l : Published[a.TaskId] = new()).Add(names.GetValueOrDefault(a.ParticipantId, $"#{a.ParticipantId}"));
        foreach (var d in await _db.TaskAllocationDrafts.Where(d => d.EventId == eventId && d.TargetRole == ParticipantRole.Volunteer)
                     .Select(d => new { d.TaskId, d.ParticipantId }).ToListAsync(ct))
            (Queued.TryGetValue(d.TaskId, out var l) ? l : Queued[d.TaskId] = new()).Add(names.GetValueOrDefault(d.ParticipantId, $"#{d.ParticipantId}"));

        Days = VolunteerCalendarLayout.Build(tasks.Select(t => new CalendarTaskInput(
            t.Id, ColumnNames.ContainsKey(t.CategoryId) ? t.CategoryId : 0, t.DueDate, VolunteerTaskWindow.WindowOf(t))));
        Current = Days.FirstOrDefault(d => d.Day == Day) ?? Days.FirstOrDefault();
        return Page();
    }

    public int Filled(int taskId) => (Published.TryGetValue(taskId, out var p) ? p.Count : 0) + (Queued.TryGetValue(taskId, out var q) ? q.Count : 0);
    public double Top(CalendarDay d, TimeOnly t) => (t - d.From).TotalMinutes * PxPerMinute;
    public double Height(TimeWindow w) => Math.Max(22, w.Hours * 60 * PxPerMinute - 2);
}
