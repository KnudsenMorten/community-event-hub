using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Export;
using CommunityHub.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Excel-like organizer table for tasks (CONTEXT.md - organizer data
/// management). Sortable, filterable, with inline per-row editing (title,
/// due date, state, assignee) and a CSV export. Organizer-only.
///
/// This is a list/grid that gives the working feel of a spreadsheet - scan,
/// filter, edit in place - without being a free-form grid that would fight
/// the data's validation rules.
/// </summary>
[Authorize]
public class TasksTableModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public TasksTableModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public List<ParticipantTask> Tasks { get; private set; } = new();
    public List<Participant> Assignees { get; private set; } = new();
    public string? Message { get; private set; }

    /// <summary>Sort column: "title", "due", "state".</summary>
    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "due";

    /// <summary>Filter by state, or null for all.</summary>
    [BindProperty(SupportsGet = true)]
    public TaskState? StateFilter { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer)
        {
            AccessDenied = true;
            return Page();
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Save one edited row.</summary>
    public async Task<IActionResult> OnPostSaveRowAsync(
        int taskId, string title, DateOnly? dueDate,
        TaskState state, int? assigneeId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me))
        {
            AccessDenied = true;
            return Page();
        }

        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.Id == taskId && t.EventId == me.EventId, ct);
        if (task is not null)
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                task.Title = title.Trim();
            }
            task.DueDate = dueDate;

            // If the row moved to/from Done, keep CompletedAt consistent.
            if (state == TaskState.Done && task.State != TaskState.Done)
            {
                task.CompletedAt = _clock.GetUtcNow();
            }
            else if (state != TaskState.Done)
            {
                task.CompletedAt = null;
            }
            task.State = state;

            // Assignee must belong to this edition (or be cleared).
            if (assigneeId is null)
            {
                task.AssignedParticipantId = null;
            }
            else
            {
                var valid = await _db.Participants.AnyAsync(
                    p => p.Id == assigneeId.Value && p.EventId == me.EventId, ct);
                if (valid)
                {
                    task.AssignedParticipantId = assigneeId;
                }
            }

            await _db.SaveChangesAsync(ct);
            Message = "Row saved.";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Export the current (filtered) task list as CSV.</summary>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me))
        {
            return Forbid();
        }

        var csv = await BuildExportCsvAsync(me.EventId, ct);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "tasks.csv");
    }

    /// <summary>Export the current (filtered) task list as Excel (.xlsx).</summary>
    public async Task<IActionResult> OnGetExportXlsxAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me))
        {
            return Forbid();
        }

        var csv = await BuildExportCsvAsync(me.EventId, ct);
        return File(CsvToXlsx.Build(csv, "Tasks"), CsvToXlsx.ContentType, "tasks.xlsx");
    }

    private async Task<string> BuildExportCsvAsync(int eventId, CancellationToken ct)
    {
        await LoadAsync(eventId, ct);

        var header = new[] { "Id", "Title", "Due date", "State", "Assignee", "Source" };
        var rows = Tasks.Select(t => (IReadOnlyList<string>)new[]
        {
            t.Id.ToString(),
            t.Title,
            t.DueDate?.ToString("dd/MM/yyyy") ?? string.Empty,
            t.State.ToString(),
            t.AssignedParticipant?.FullName ?? string.Empty,
            t.SourceKey ?? string.Empty,
        });

        return CsvWriter.Write(header, rows);
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var query = _db.Tasks
            .Include(t => t.AssignedParticipant)
            .Where(t => t.EventId == eventId);

        if (StateFilter is not null)
        {
            query = query.Where(t => t.State == StateFilter.Value);
        }

        query = Sort switch
        {
            "title" => query.OrderBy(t => t.Title),
            "state" => query.OrderBy(t => t.State).ThenBy(t => t.DueDate),
            _ => query.OrderBy(t => t.DueDate).ThenBy(t => t.Title),
        };

        Tasks = await query.ToListAsync(ct);

        Assignees = await _db.Participants
            .Where(p => p.EventId == eventId && p.IsActive)
            .OrderBy(p => p.FullName)
            .ToListAsync(ct);
    }
}
