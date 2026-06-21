using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Volunteer;

/// <summary>
/// VOLUNTEER "My tasks" view: the tasks a volunteer is assigned to, grouped
/// Category → Subcategory, with the ability to mark their own task done/in-progress
/// and to <b>ask their category's supervisor for help</b>. All mutations go through
/// <see cref="VolunteerStructureService"/>, which enforces that a volunteer may
/// only touch tasks they are assigned to.
/// </summary>
[Authorize]
public class MyTasksModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VolunteerStructureService _svc;
    private readonly VolunteerHelpNotificationService _helpNotify;
    private readonly ILogger<MyTasksModel> _logger;

    public MyTasksModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        VolunteerStructureService svc,
        VolunteerHelpNotificationService helpNotify,
        ILogger<MyTasksModel> logger)
    {
        _db = db;
        _participant = participant;
        _svc = svc;
        _helpNotify = helpNotify;
        _logger = logger;
    }

    /// <summary>
    /// Statuses a volunteer may set from their own self-service surface. Cancelled
    /// ("No longer needed") is a coordinator/supervisor-only state and is excluded
    /// here so it never appears in — nor is accepted from — the volunteer dropdown.
    /// </summary>
    public static readonly IReadOnlyList<VolunteerTaskStatus> VolunteerSelectableStatuses =
        new[] { VolunteerTaskStatus.Open, VolunteerTaskStatus.InProgress, VolunteerTaskStatus.Done };

    public string? Notice { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    /// <summary>Tasks grouped Category → Subcategory for the view.</summary>
    public List<CategoryGroup> Groups { get; private set; } = new();
    /// <summary>The volunteer's own open help requests (so they can see replies).</summary>
    public List<VolunteerHelpRequest> MyHelp { get; private set; } = new();

    public record TaskRow(int Id, string Title, string? Shift, DateOnly? Due, VolunteerTaskStatus Status,
        string SupervisorName, string? Instructions, string? Prerequisites, string? Expectations);
    public record SubGroup(string Subcategory, List<TaskRow> Tasks);
    public record CategoryGroup(string Category, string Supervisors, string? EldkLeadName, List<SubGroup> Subs);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Notice = Msg;
        await LoadAsync(me, ct);
        return Page();
    }

    private VolunteerStructureService.ActorContext Actor(CurrentParticipant me)
        => new(me.ParticipantId, me.Email, me.Role, me.EventId);

    public async Task<IActionResult> OnPostSetStatusAsync(int taskId, VolunteerTaskStatus status, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Defense-in-depth: a volunteer may not set Cancelled ("No longer needed")
        // from their self-service surface — that is a coordinator/supervisor action.
        // Reject it server-side regardless of what the posted dropdown contained.
        if (!VolunteerSelectableStatuses.Contains(status))
        {
            return RedirectToPage(new { Msg = "You cannot set that status. Ask your supervisor if a task is no longer needed." });
        }

        try
        {
            await _svc.SetTaskStatusAsync(Actor(me), taskId, status, ct);
            return RedirectToPage(new { Msg = "Task updated." });
        }
        catch (VolunteerValidationException ex) { return RedirectToPage(new { Msg = ex.Message }); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    public async Task<IActionResult> OnPostRaiseHelpAsync(int taskId, string message, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try
        {
            var req = await _svc.RaiseHelpAsync(Actor(me), taskId, message, ct);

            // Best-effort: notify the category's supervisor (and organizer lead
            // for oversight) by email. The DEV redirect is applied inside the
            // email sender. A mail failure must NOT fail the help-raise — the
            // request is already saved and shows in the supervisor's in-hub inbox.
            try
            {
                await _helpNotify.NotifySupervisorAsync(me.EventId, req.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Help request {HelpId} saved but supervisor notification failed.", req.Id);
            }

            return RedirectToPage(new { Msg = "Your supervisor has been asked for help." });
        }
        catch (VolunteerValidationException ex) { return RedirectToPage(new { Msg = ex.Message }); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        var tasks = await _svc.LoadMyTasksAsync(me.EventId, me.ParticipantId, ct);

        // Resolve each owning bucket's supervisor(s) (multi) + ELDK lead.
        var catIds = tasks.Select(t => t.Subcategory.CategoryId).Distinct().ToList();
        var eldkLeadByCat = await _db.VolunteerCategories
            .Where(c => catIds.Contains(c.Id))
            .Select(c => new { c.Id, c.EldkLeadName, CatName = c.Name })
            .ToDictionaryAsync(x => x.Id, x => x, ct);

        var supByCat = new Dictionary<int, string>();
        foreach (var catId in catIds)
        {
            var sups = await _svc.LoadSupervisorsAsync(me.EventId, catId, ct);
            var names = sups
                .Select(p => string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName)
                .ToList();
            supByCat[catId] = names.Count > 0 ? string.Join(", ", names) : "your supervisor";
        }

        Groups = tasks
            .GroupBy(t => t.Subcategory.CategoryId)
            .Select(cg =>
            {
                var meta = eldkLeadByCat.TryGetValue(cg.Key, out var m) ? m : null;
                return new CategoryGroup(
                    meta?.CatName ?? cg.First().Subcategory.Category.Name,
                    supByCat.TryGetValue(cg.Key, out var sup) ? sup : "your supervisor",
                    meta?.EldkLeadName,
                    cg.GroupBy(t => t.Subcategory.Name)
                      .Select(sg => new SubGroup(
                          sg.Key,
                          sg.Select(t => new TaskRow(
                              t.Id, t.Title, t.Shift, t.DueDate, t.Status,
                              supByCat.TryGetValue(t.Subcategory.CategoryId, out var s) ? s : "your supervisor",
                              t.Instructions, t.Prerequisites, t.Expectations))
                            .OrderBy(t => t.Title).ToList()))
                      .OrderBy(s => s.Subcategory).ToList());
            })
            .OrderBy(c => c.Category)
            .ToList();

        MyHelp = await _db.VolunteerHelpRequests
            .Where(h => h.EventId == me.EventId && h.RequestedByParticipantId == me.ParticipantId)
            .Include(h => h.Task)
            .OrderByDescending(h => h.CreatedAt)
            .ToListAsync(ct);
    }
}
