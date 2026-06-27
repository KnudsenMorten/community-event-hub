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
/// ORGANIZER "Edit task" (§151) — the SHARED task-definition editor. Any organizer
/// may open any task and edit its content (title, detailed description, criticality,
/// responsible team, resources-needed, pre-req, expectations, instructions, ELDK lead,
/// time window, due date); editing is NOT scoped to whoever created the task. All
/// mutations go through <see cref="VolunteerStructureService.UpdateTaskContentAsync"/>,
/// which enforces the organizer/supervisor permission model in one place and
/// auto-generates the detailed description from the title when left blank. The
/// per-organizer draft allocation queue is untouched by this page.
/// </summary>
[Authorize]
public class EditVolunteerTaskModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VolunteerStructureService _structure;
    private readonly ITaskGuidanceGenerator _guidance;
    private readonly ILogger<EditVolunteerTaskModel> _logger;

    public EditVolunteerTaskModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        VolunteerStructureService structure,
        ITaskGuidanceGenerator guidance,
        ILogger<EditVolunteerTaskModel> logger)
    {
        _db = db;
        _participant = participant;
        _structure = structure;
        _guidance = guidance;
        _logger = logger;
    }

    public bool AccessDenied { get; private set; }
    public string? Notice { get; private set; }
    public string? Error { get; private set; }
    public bool AiEnabled { get; private set; }

    /// <summary>The task currently being edited (null when picking from the list).</summary>
    public TaskView? Editing { get; private set; }

    /// <summary>All tasks in the edition, for the picker shown when no task is selected.</summary>
    public List<TaskRow> AllTasks { get; private set; } = new();

    [BindProperty(SupportsGet = true)] public int? TaskId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    [BindProperty] public InputModel Input { get; set; } = new();

    public record TaskView(int Id, string Bucket, string Subcategory);
    public record TaskRow(int Id, string Title, string Bucket, bool HasDescription);

    /// <summary>The editable shared task definition. Mirrors the
    /// <see cref="VolunteerStructureService.UpdateTaskContentAsync"/> parameters.</summary>
    public class InputModel
    {
        public int TaskId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public VolunteerTaskCriticality Criticality { get; set; } = VolunteerTaskCriticality.Unspecified;
        public string? ResponsibleTeam { get; set; }
        public int ResourcesNeeded { get; set; }
        public string? Prerequisites { get; set; }
        public string? Expectations { get; set; }
        public string? Instructions { get; set; }
        public string? EldkLeadName { get; set; }
        public string? TimeEnd { get; set; }
        public string? Shift { get; set; }
        public DateOnly? DueDate { get; set; }
    }

    private VolunteerStructureService.ActorContext Actor(CurrentParticipant me)
        => new(me.ParticipantId, me.Email, me.Role, me.EventId);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Notice = Msg;
        AiEnabled = _guidance.IsAiBacked;

        if (TaskId is int id)
        {
            if (!await LoadTaskAsync(me, id, ct))
                return RedirectToPage(new { Msg = "Task not found in this edition." });
        }
        else
        {
            await LoadListAsync(me, ct);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        // Any REAL organizer may edit the shared definition (acting-as cannot write).
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        try
        {
            var ok = await _structure.UpdateTaskContentAsync(
                Actor(me), Input.TaskId,
                title: Input.Title,
                description: Input.Description,
                criticality: Input.Criticality,
                responsibleTeam: Input.ResponsibleTeam,
                resourcesNeeded: Input.ResourcesNeeded,
                prerequisites: Input.Prerequisites,
                dueDate: Input.DueDate,
                expectations: Input.Expectations,
                instructions: Input.Instructions,
                eldkLeadName: Input.EldkLeadName,
                timeEnd: Input.TimeEnd,
                shift: Input.Shift,
                ct: ct);
            if (!ok) return RedirectToPage(new { Msg = "Task not found in this edition." });
            return RedirectToPage(new { TaskId = Input.TaskId, Msg = "Task saved." });
        }
        catch (VolunteerValidationException ex)
        {
            _logger.LogInformation("Task edit rejected for task {TaskId}: {Message}", Input.TaskId, ex.Message);
            AiEnabled = _guidance.IsAiBacked;
            Error = ex.Message;
            await LoadTaskAsync(me, Input.TaskId, ct);
            return Page();
        }
        catch (VolunteerAccessDeniedException)
        {
            return Forbid();
        }
    }

    private async Task<bool> LoadTaskAsync(CurrentParticipant me, int taskId, CancellationToken ct)
    {
        var task = await _db.VolunteerTasks
            .Include(t => t.Subcategory).ThenInclude(s => s.Category)
            .FirstOrDefaultAsync(t => t.Id == taskId && t.EventId == me.EventId, ct);
        if (task is null) return false;

        Editing = new TaskView(task.Id, task.Subcategory.Category.Name, task.Subcategory.Name);
        Input = new InputModel
        {
            TaskId = task.Id,
            Title = task.Title,
            Description = task.Description,
            Criticality = task.Criticality,
            ResponsibleTeam = task.ResponsibleTeam,
            ResourcesNeeded = task.ResourcesNeeded,
            Prerequisites = task.Prerequisites,
            Expectations = task.Expectations,
            Instructions = task.Instructions,
            EldkLeadName = task.EldkLeadName,
            TimeEnd = task.TimeEnd,
            Shift = task.Shift,
            DueDate = task.DueDate,
        };
        return true;
    }

    private async Task LoadListAsync(CurrentParticipant me, CancellationToken ct)
    {
        var tasks = await _db.VolunteerTasks
            .Where(t => t.EventId == me.EventId)
            .Include(t => t.Subcategory).ThenInclude(s => s.Category)
            .ToListAsync(ct);

        AllTasks = tasks
            .Select(t => new TaskRow(
                t.Id, t.Title, t.Subcategory.Category.Name,
                !string.IsNullOrWhiteSpace(t.Description)))
            .OrderBy(t => t.Bucket)
            .ThenBy(t => t.Title)
            .ToList();
    }
}
