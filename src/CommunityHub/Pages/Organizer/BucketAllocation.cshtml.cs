using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ORGANIZER "Buckets &amp; allocation" — the task-mapper. Import the plan CSV,
/// see each task's red/green resource gap, queue people→tasks into a DRAFT and watch
/// the simulation update, then COMMIT the draft into real assignments (or discard).
/// All mutations go through <see cref="VolunteerAllocationService"/> /
/// <see cref="VolunteerPlanImportService"/>, which enforce organizer-only access.
/// </summary>
[Authorize]
public class BucketAllocationModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VolunteerAllocationService _alloc;
    private readonly VolunteerPlanImportService _import;
    private readonly VolunteerPlanParser _parser;
    private readonly ITaskGuidanceGenerator _guidance;
    private readonly ILogger<BucketAllocationModel> _logger;

    public BucketAllocationModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        VolunteerAllocationService alloc,
        VolunteerPlanImportService import,
        VolunteerPlanParser parser,
        ITaskGuidanceGenerator guidance,
        ILogger<BucketAllocationModel> logger)
    {
        _db = db;
        _participant = participant;
        _alloc = alloc;
        _import = import;
        _parser = parser;
        _guidance = guidance;
        _logger = logger;
    }

    public bool AccessDenied { get; private set; }
    public string? Notice { get; private set; }
    public bool AiEnabled { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    /// <summary>Tasks grouped by Bucket, each with live coverage (red/green).</summary>
    public List<BucketGroup> Buckets { get; private set; } = new();
    public List<SelectListItem> VolunteerOptions { get; private set; } = new();
    /// <summary>The organizer's current draft queue (pending allocations).</summary>
    public List<TaskAllocationDraft> Draft { get; private set; } = new();
    public int DraftCount => Draft.Count;

    public record TaskCard(int Id, string Title, TaskCoverage Coverage, string? ResponsibleTeam,
        VolunteerTaskStatus Status, string? EldkLeadName);
    public record BucketGroup(int Id, string Name, string? EldkLeadName, List<TaskCard> Tasks);

    private VolunteerStructureService.ActorContext Actor(CurrentParticipant me)
        => new(me.ParticipantId, me.Email, me.Role, me.EventId);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Notice = Msg;
        await LoadAsync(me, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? planFile, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        if (planFile is null || planFile.Length == 0)
            return RedirectToPage(new { Msg = "Please choose a plan CSV to import." });

        try
        {
            string csv;
            using (var reader = new StreamReader(planFile.OpenReadStream(), Encoding.UTF8))
                csv = await reader.ReadToEndAsync(ct);

            var plan = _parser.Parse(csv);
            var result = await _import.ImportAsync(me.EventId, plan, fillGuidance: true, ct);

            var msg = $"Imported {result.TasksCreated} tasks into {result.BucketsCreated} buckets; " +
                      $"{result.AssignmentsLinked} people linked.";
            if (result.NamesUnmatched > 0)
                msg += $" {result.NamesUnmatched} name(s) could not be matched to a volunteer.";
            return RedirectToPage(new { Msg = msg });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plan import failed.");
            return RedirectToPage(new { Msg = "Import failed: " + ex.Message });
        }
    }

    public async Task<IActionResult> OnPostAddDraftAsync(int taskId, int volunteerId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try { await _alloc.AddDraftAsync(Actor(me), taskId, volunteerId, ct); return RedirectToPage(new { Msg = "Added to draft." }); }
        catch (VolunteerValidationException ex) { return RedirectToPage(new { Msg = ex.Message }); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    public async Task<IActionResult> OnPostRemoveDraftAsync(int taskId, int volunteerId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try { await _alloc.RemoveDraftAsync(Actor(me), taskId, volunteerId, ct); return RedirectToPage(new { Msg = "Removed from draft." }); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    public async Task<IActionResult> OnPostCommitAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try
        {
            var r = await _alloc.CommitAsync(Actor(me), ct);
            return RedirectToPage(new { Msg = $"Committed {r.Committed} allocation(s)." });
        }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    public async Task<IActionResult> OnPostDiscardAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try { var n = await _alloc.DiscardAsync(Actor(me), ct); return RedirectToPage(new { Msg = $"Discarded {n} draft allocation(s)." }); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    public async Task<IActionResult> OnPostCompleteAsync(int taskId, [FromServices] VolunteerStructureService svc, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try { await svc.MarkTaskCompletedByLeadAsync(Actor(me), taskId, ct); return RedirectToPage(new { Msg = "Task marked completed." }); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    public async Task<IActionResult> OnPostRegenerateGuidanceAsync(int taskId, [FromServices] VolunteerStructureService svc, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var task = await _db.VolunteerTasks
            .Include(t => t.Subcategory).ThenInclude(s => s.Category)
            .FirstOrDefaultAsync(t => t.Id == taskId && t.EventId == me.EventId, ct);
        if (task is null) return RedirectToPage(new { Msg = "Task not found." });

        var g = await _guidance.GenerateAsync(task.Title, task.Subcategory.Category.Name, task.ResponsibleTeam, ct);
        await svc.UpdateTaskDetailsAsync(Actor(me), taskId,
            prerequisites: g.Prerequisites, expectations: g.Expectations,
            // keep the rest of the fields as-is
            resourcesNeeded: task.ResourcesNeeded, criticality: task.Criticality,
            responsibleTeam: task.ResponsibleTeam, eldkLeadName: task.EldkLeadName,
            instructions: task.Instructions, timeEnd: task.TimeEnd, ct: ct);
        return RedirectToPage(new { Msg = "Guidance regenerated." });
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        AiEnabled = _guidance.IsAiBacked;
        var actor = Actor(me);

        // Coverage for every task (live simulation including this organizer's draft).
        var coverage = (await _alloc.LoadCoverageAsync(actor, ct))
            .ToDictionary(c => c.TaskId, c => c);

        var tasks = await _db.VolunteerTasks
            .Where(t => t.EventId == me.EventId)
            .Include(t => t.Subcategory).ThenInclude(s => s.Category)
            .ToListAsync(ct);

        Buckets = tasks
            .GroupBy(t => t.Subcategory.Category)
            .Select(g => new BucketGroup(
                g.Key.Id, g.Key.Name, g.Key.EldkLeadName,
                g.Select(t => new TaskCard(
                        t.Id, t.Title,
                        coverage.TryGetValue(t.Id, out var c) ? c : new TaskCoverage(t.Id, t.Title, t.ResourcesNeeded, 0, 0),
                        t.ResponsibleTeam, t.Status, t.EldkLeadName))
                    .OrderBy(t => t.Title).ToList()))
            .OrderBy(b => b.Name)
            .ToList();

        VolunteerOptions = await _db.Participants
            .Where(p => p.EventId == me.EventId && p.Role == ParticipantRole.Volunteer && p.IsActive)
            .OrderBy(p => p.FullName)
            .Select(p => new SelectListItem(
                p.FullName != "" ? p.FullName : p.Email, p.Id.ToString()))
            .ToListAsync(ct);

        Draft = await _alloc.LoadDraftAsync(actor, ct);
    }
}
