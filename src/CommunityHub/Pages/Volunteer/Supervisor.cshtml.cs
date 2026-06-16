using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Volunteer;

/// <summary>
/// SUPERVISOR dashboard: the elevated view for a volunteer who has been appointed
/// to run one or more categories. They can manage THEIR categories' subcategories
/// and tasks, assign volunteers, and answer help requests — scoped server-side by
/// <see cref="VolunteerStructureService"/> to exactly the categories they
/// supervise. A volunteer who supervises nothing sees an empty-state.
/// </summary>
[Authorize]
public class SupervisorModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VolunteerStructureService _svc;

    public SupervisorModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        VolunteerStructureService svc)
    {
        _db = db;
        _participant = participant;
        _svc = svc;
    }

    public bool NotASupervisor { get; private set; }
    public string? Notice { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    public List<VolunteerCategory> MyCategories { get; private set; } = new();
    public Dictionary<int, List<VolunteerHelpRequest>> HelpByCategory { get; private set; } = new();
    public List<SelectListItem> VolunteerOptions { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        // Only a volunteer can be a supervisor; organizers use the organizer page.
        if (me.Role != ParticipantRole.Volunteer) { NotASupervisor = true; return Page(); }

        Notice = Msg;
        await LoadAsync(me, ct);
        return Page();
    }

    private VolunteerStructureService.ActorContext? Actor()
    {
        var me = _participant.Current;
        if (me is null || me.Role != ParticipantRole.Volunteer) return null;
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

    public Task<IActionResult> OnPostCreateSubcategoryAsync(int categoryId, string name, CancellationToken ct)
        => RunAsync(async a => { await _svc.CreateSubcategoryAsync(a, categoryId, name, null, ct); return $"Subcategory '{name}' added."; });

    public Task<IActionResult> OnPostCreateTaskAsync(int subcategoryId, string title, DateOnly? due, string? shift, CancellationToken ct)
        => RunAsync(async a => { await _svc.CreateTaskAsync(a, subcategoryId, title, null, due, shift, ct: ct); return $"Task '{title}' added."; });

    public Task<IActionResult> OnPostAssignAsync(int taskId, int volunteerParticipantId, CancellationToken ct)
        => RunAsync(async a => { await _svc.AssignVolunteerAsync(a, taskId, volunteerParticipantId, ct); return "Volunteer assigned."; });

    public Task<IActionResult> OnPostSetTaskStatusAsync(int taskId, VolunteerTaskStatus status, CancellationToken ct)
        => RunAsync(async a => { await _svc.SetTaskStatusAsync(a, taskId, status, ct); return "Task status updated."; });

    public Task<IActionResult> OnPostAnswerHelpAsync(int helpRequestId, string response, VolunteerHelpStatus status, CancellationToken ct)
        => RunAsync(async a => { await _svc.AnswerHelpAsync(a, helpRequestId, response, status, ct); return "Help request answered."; });

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        MyCategories = await _svc.LoadSupervisedCategoriesAsync(me.EventId, me.ParticipantId, ct);
        if (MyCategories.Count == 0) { NotASupervisor = true; return; }

        HelpByCategory = new();
        foreach (var cat in MyCategories)
        {
            HelpByCategory[cat.Id] = await _svc.LoadHelpForCategoryAsync(me.EventId, cat.Id, ct);
        }

        VolunteerOptions = await _db.Participants
            .Where(p => p.EventId == me.EventId && p.IsActive && p.Role == ParticipantRole.Volunteer)
            .OrderBy(p => p.FullName)
            .Select(p => new SelectListItem(
                string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName, p.Id.ToString()))
            .ToListAsync(ct);
    }
}
