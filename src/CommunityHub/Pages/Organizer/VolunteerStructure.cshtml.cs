using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ORGANIZER view of the volunteer work structure: build the 3-level tree
/// (Category → Subcategory → Task), name each category's volunteer LEAD (an
/// organizer) and APPOINT its SUPERVISOR (a volunteer from the pool — appointing
/// elevates them to category-scoped management), assign volunteers to tasks, and
/// see coverage at a glance. All mutations go through
/// <see cref="VolunteerStructureService"/>, which enforces the permission model
/// server-side, so this page only resolves the signed-in organizer and relays.
/// </summary>
[Authorize]
public class VolunteerStructureModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VolunteerStructureService _svc;
    private readonly CommunityHub.Core.Organizer.VolunteerTaskBulkOperationService _bulk;

    public VolunteerStructureModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        VolunteerStructureService svc,
        CommunityHub.Core.Organizer.VolunteerTaskBulkOperationService bulk)
    {
        _db = db;
        _participant = participant;
        _svc = svc;
        _bulk = bulk;
    }

    /// <summary>The task ids ticked in the bulk-select grid (posted form field).</summary>
    [BindProperty] public List<int> SelectedTaskIds { get; set; } = new();

    /// <summary>The status a bulk change-status applies (posted form field).</summary>
    [BindProperty] public VolunteerTaskStatus BulkStatus { get; set; }

    public bool AccessDenied { get; private set; }
    public string? Notice { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    public List<VolunteerCategory> Tree { get; private set; } = new();
    /// <summary>Organizers in the edition (candidate leads).</summary>
    public List<SelectListItem> OrganizerOptions { get; private set; } = new();
    /// <summary>Volunteers in the edition (candidate supervisors / assignees).</summary>
    public List<SelectListItem> VolunteerOptions { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Notice = Msg;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private VolunteerStructureService.ActorContext? Actor()
    {
        var me = _participant.Current;
        if (me is null || me.Role != ParticipantRole.Organizer) return null;
        return new VolunteerStructureService.ActorContext(
            me.ParticipantId, me.Email, me.Role, me.EventId);
    }

    private async Task<IActionResult> RunAsync(Func<VolunteerStructureService.ActorContext, Task<string>> op)
    {
        var actor = Actor();
        if (actor is null) return Forbid();
        try
        {
            var msg = await op(actor.Value);
            return RedirectToPage(new { Msg = msg });
        }
        catch (VolunteerValidationException ex) { return RedirectToPage(new { Msg = ex.Message }); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    public Task<IActionResult> OnPostCreateCategoryAsync(string name, string? description, CancellationToken ct)
        => RunAsync(async a => { await _svc.CreateCategoryAsync(a, name, description, ct); return $"Category '{name}' created."; });

    public Task<IActionResult> OnPostRenameCategoryAsync(int categoryId, string name, string? description, CancellationToken ct)
        => RunAsync(async a => { await _svc.RenameCategoryAsync(a, categoryId, name, description, ct); return "Category updated."; });

    public Task<IActionResult> OnPostDeleteCategoryAsync(int categoryId, CancellationToken ct)
        => RunAsync(async a => { await _svc.DeleteCategoryAsync(a, categoryId, ct); return "Category removed."; });

    public Task<IActionResult> OnPostSetLeadAsync(int categoryId, int? leadParticipantId, CancellationToken ct)
        => RunAsync(async a => { await _svc.SetLeadAsync(a, categoryId, leadParticipantId, ct); return "Lead updated."; });

    public Task<IActionResult> OnPostAppointSupervisorAsync(int categoryId, int? supervisorParticipantId, CancellationToken ct)
        => RunAsync(async a => { await _svc.AppointSupervisorAsync(a, categoryId, supervisorParticipantId, ct); return "Supervisor updated."; });

    public Task<IActionResult> OnPostCreateSubcategoryAsync(int categoryId, string name, string? description, CancellationToken ct)
        => RunAsync(async a => { await _svc.CreateSubcategoryAsync(a, categoryId, name, description, ct); return $"Subcategory '{name}' added."; });

    public Task<IActionResult> OnPostDeleteSubcategoryAsync(int subcategoryId, CancellationToken ct)
        => RunAsync(async a => { await _svc.DeleteSubcategoryAsync(a, subcategoryId, ct); return "Subcategory removed."; });

    public Task<IActionResult> OnPostCreateTaskAsync(int subcategoryId, string title, string? description, DateOnly? due, string? shift, CancellationToken ct)
        => RunAsync(async a => { await _svc.CreateTaskAsync(a, subcategoryId, title, description, due, shift, ct: ct); return $"Task '{title}' added."; });

    public Task<IActionResult> OnPostDeleteTaskAsync(int taskId, CancellationToken ct)
        => RunAsync(async a => { await _svc.DeleteTaskAsync(a, taskId, ct); return "Task removed."; });

    public Task<IActionResult> OnPostAssignAsync(int taskId, int volunteerParticipantId, CancellationToken ct)
        => RunAsync(async a => { await _svc.AssignVolunteerAsync(a, taskId, volunteerParticipantId, ct); return "Volunteer assigned."; });

    public Task<IActionResult> OnPostUnassignAsync(int taskId, int volunteerParticipantId, CancellationToken ct)
        => RunAsync(async a => { await _svc.UnassignVolunteerAsync(a, taskId, volunteerParticipantId, ct); return "Volunteer unassigned."; });

    /// <summary>
    /// BULK change-status across the ticked tasks (§20 universal CRUD + bulk). The
    /// safe semantics live in
    /// <see cref="CommunityHub.Core.Organizer.VolunteerTaskBulkOperationService"/>:
    /// event-scoped, idempotent, honest change-count. Organizer-only (page-gated);
    /// the page's confirm modal (live count) gates the click.
    /// </summary>
    public async Task<IActionResult> OnPostBulkStatusAsync(CancellationToken ct)
    {
        var actor = Actor();
        if (actor is null) return Forbid();

        var requested = SelectedTaskIds.Where(id => id > 0).Distinct().Count();
        if (requested == 0)
            return RedirectToPage(new { Msg = "Pick at least one task first." });

        var result = await _bulk.ChangeStatusAsync(actor.Value.EventId, SelectedTaskIds, BulkStatus, ct);
        var skipped = result.Skipped(requested);
        var msg = $"{result.Changed} task(s) set to {BulkStatus}"
            + (result.Matched - result.Changed > 0 ? $", {result.Matched - result.Changed} already in that status" : string.Empty)
            + (skipped > 0 ? $", {skipped} not found" : string.Empty)
            + ".";
        return RedirectToPage(new { Msg = msg });
    }

    /// <summary>
    /// BULK delete across the ticked tasks. Linked-data-safe: tasks with help-request
    /// history are left untouched and reported; clean tasks (with their import-state
    /// assignments) are removed in one transaction. Organizer-only; confirm-gated.
    /// </summary>
    public async Task<IActionResult> OnPostBulkDeleteTasksAsync(CancellationToken ct)
    {
        var actor = Actor();
        if (actor is null) return Forbid();

        var requested = SelectedTaskIds.Where(id => id > 0).Distinct().Count();
        if (requested == 0)
            return RedirectToPage(new { Msg = "Pick at least one task first." });

        var result = await _bulk.DeleteAsync(actor.Value.EventId, SelectedTaskIds, ct);
        var skipped = result.Skipped(requested);
        var msg = $"{result.Deleted} task(s) deleted"
            + (result.Blocked > 0 ? $", {result.Blocked} kept (have help-request history)" : string.Empty)
            + (skipped > 0 ? $", {skipped} not found" : string.Empty)
            + ".";
        return RedirectToPage(new { Msg = msg });
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Tree = await _svc.LoadTreeAsync(eventId, ct);

        var people = await _db.Participants
            .Where(p => p.EventId == eventId && p.IsActive
                        && (p.Role == ParticipantRole.Organizer || p.Role == ParticipantRole.Volunteer))
            .OrderBy(p => p.FullName)
            .Select(p => new { p.Id, p.FullName, p.Email, p.Role })
            .ToListAsync(ct);

        OrganizerOptions = people.Where(p => p.Role == ParticipantRole.Organizer)
            .Select(p => new SelectListItem(
                string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName, p.Id.ToString()))
            .ToList();
        VolunteerOptions = people.Where(p => p.Role == ParticipantRole.Volunteer)
            .Select(p => new SelectListItem(
                string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName, p.Id.ToString()))
            .ToList();
    }
}
