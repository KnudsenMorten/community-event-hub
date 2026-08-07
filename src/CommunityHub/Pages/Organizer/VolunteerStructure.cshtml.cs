using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
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
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;
    private readonly CommunityHub.Core.Settings.RingResolver _rings;

    public VolunteerStructureModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        VolunteerStructureService svc,
        CommunityHub.Core.Organizer.VolunteerTaskBulkOperationService bulk,
        CommunityHub.Core.Settings.FeatureGateService gate,
        CommunityHub.Core.Settings.RingResolver rings)
    {
        _db = db;
        _participant = participant;
        _svc = svc;
        _bulk = bulk;
        _gate = gate;
        _rings = rings;
    }

    /// <summary>The task ids ticked in the bulk-select grid (posted form field).</summary>
    [BindProperty] public List<int> SelectedTaskIds { get; set; } = new();

    /// <summary>The status a bulk change-status applies (posted form field).</summary>
    [BindProperty] public VolunteerTaskStatus BulkStatus { get; set; }

    /// <summary>
    /// §942 — the bulk-MOVE destination, as <c>c:&lt;categoryId&gt;</c> (whole category) or
    /// <c>s:&lt;subcategoryId&gt;</c> (one sub-category). A prefixed string rather than two fields:
    /// it is ONE choice in one dropdown, and two ids would allow the invalid state where both are set.
    /// </summary>
    [BindProperty] public string? MoveTarget { get; set; }

    public bool AccessDenied { get; private set; }
    public string? Notice { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    public List<VolunteerCategory> Tree { get; private set; } = new();
    /// <summary>§199 — every defined volunteer task (flat, read-only) for the
    /// "All volunteer tasks" review section, with full detail.</summary>
    public List<VolunteerTask> AllTasks { get; private set; } = new();
    /// <summary>Organizers in the edition (candidate leads).</summary>
    public List<SelectListItem> OrganizerOptions { get; private set; } = new();
    /// <summary>Volunteers in the edition (candidate supervisors / assignees).</summary>
    public List<SelectListItem> VolunteerOptions { get; private set; } = new();

    /// <summary>
    /// §942 — every sub-category as a bulk-MOVE target, labelled <c>Category → Sub-category</c>.
    /// The category has to be in the label: sub-category names are only unique WITHIN a category
    /// ("General" can exist under three of them), so a bare name would make the organizer guess
    /// which one they are moving 127 tasks into.
    /// </summary>
    public List<SelectListItem> SubcategoryOptions { get; private set; } = new();

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
        // Used ONLY by the OnPost* (write) handlers — the OnGet view keeps its own
        // role-only gate. Writes require a REAL organizer: an acting-as / secretary
        // session carries Role==Organizer but must never mutate (§234 / OrganizerAuth).
        var me = _participant.Current;
        if (!OrganizerAuth.IsRealOrganizer(me)) return null;
        return new VolunteerStructureService.ActorContext(
            me!.ParticipantId, me.Email, me.Role, me.EventId);
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

    // §334: deleting a category takes every subcategory and every task under it. With 127
    // imported volunteer tasks / 229 slots that is a whole area of the event in one click, so
    // the phrase is verified HERE rather than by the row's confirm().
    public Task<IActionResult> OnPostDeleteCategoryAsync(
        int categoryId, string? confirmPhrase, CancellationToken ct)
    {
        if (!TypedConfirmation.Matches(confirmPhrase, TypedConfirmation.ConfirmPhrase))
        {
            return Task.FromResult<IActionResult>(RedirectToPage(new
            {
                Msg = TypedConfirmation.Rejection(
                    TypedConfirmation.ConfirmPhrase, "delete this category and everything under it"),
            }));
        }
        return RunAsync(async a => { await _svc.DeleteCategoryAsync(a, categoryId, ct); return "Category removed."; });
    }

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

    [CommunityHub.Audit.Audit("Assigned a volunteer to a task", TargetType = "VolunteerTask")]
    public Task<IActionResult> OnPostAssignAsync(int taskId, int volunteerParticipantId, CancellationToken ct)
        => RunAsync(async a =>
        {
            // RING-SCOPED (REQUIREMENTS §23a category 3): don't assign a volunteer who is
            // above the volunteer-tasks feature's released ring. With the Broad default
            // every volunteer is in scope; lower the ring in Settings to ring-test.
            if (!await _gate.IsTargetInReleasedRingAsync("volunteer-tasks", a.EventId, volunteerParticipantId, _rings, ct))
                return "That volunteer is above the volunteer-tasks feature's released ring (out of scope). Promote the ring in Settings to include them.";
            await _svc.AssignVolunteerAsync(a, taskId, volunteerParticipantId, ct);
            return "Volunteer assigned.";
        });

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

    /// <summary>
    /// §942 — BULK MOVE the ticked tasks under another sub-category. Operator 2026-08-07: the whole
    /// Excel import (127 tasks) landed in one bucket while the categories he had set up — Check-in
    /// with a lead and a supervisor already appointed — sat empty. Safe semantics live in
    /// <see cref="VolunteerTaskBulkOperationService.MoveAsync"/>: event-scoped, idempotent, target
    /// validated, and volunteer ASSIGNMENTS untouched so nobody loses their placement.
    /// </summary>
    public async Task<IActionResult> OnPostBulkMoveTasksAsync(CancellationToken ct)
    {
        var actor = Actor();
        if (actor is null) return Forbid();

        var requested = SelectedTaskIds.Where(id => id > 0).Distinct().Count();
        if (requested == 0)
            return RedirectToPage(new { Msg = "Pick at least one task first." });
        var target = (MoveTarget ?? string.Empty).Trim();
        if (target.Length < 3 || (target[0] != 'c' && target[0] != 's') || target[1] != ':'
            || !int.TryParse(target[2..], out var targetId) || targetId <= 0)
            return RedirectToPage(new { Msg = "Choose where to move them." });

        try
        {
            VolunteerTaskBulkOperationService.BulkMoveResult result;
            string landedIn;
            if (target[0] == 'c')
            {
                (result, landedIn) = await _bulk.MoveToCategoryAsync(
                    actor.Value.EventId, SelectedTaskIds, targetId, ct);
            }
            else
            {
                result = await _bulk.MoveAsync(actor.Value.EventId, SelectedTaskIds, targetId, ct);
                landedIn = "the chosen sub-category";
            }

            var skipped = result.Skipped(requested);
            // Reported the same way as the other bulk actions: what changed, what was already
            // right, and what was not found — so a partial outcome never reads as a full one.
            // 🔑 It also NAMES where they landed: with "whole category" the destination can be a
            // sub-category the organizer never picked (or one just created), and a move that does
            // not say where things went is how work goes missing.
            var msg = $"{result.Moved} task(s) moved to {landedIn}"
                + (result.AlreadyThere > 0 ? $", {result.AlreadyThere} already there" : string.Empty)
                + (skipped > 0 ? $", {skipped} not found" : string.Empty)
                + ". Volunteer assignments were kept.";
            return RedirectToPage(new { Msg = msg });
        }
        catch (InvalidOperationException ex) { return RedirectToPage(new { Msg = ex.Message }); }
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Tree = await _svc.LoadTreeAsync(eventId, ct);
        AllTasks = await _svc.LoadAllTasksAsync(eventId, ct);

        // §942 — built from the already-loaded tree, so the move targets can never disagree with
        // the structure shown on the page (and it costs no extra query).
        //
        // 🔴 EVERY CATEGORY IS OFFERED, sub-categories or not. The first live run listed exactly ONE
        // target because Check-in — the category he actually wanted to fill — had no sub-category,
        // and tasks hang off sub-categories. A picker that cannot name the destination he asked for
        // is a feature that does not do its job. "c:<id>" moves into the whole category (resolving
        // or creating its landing sub-category); "s:<id>" targets one precisely.
        SubcategoryOptions = Tree
            .OrderBy(c => c.Name)
            .SelectMany(c => new[]
                {
                    new SelectListItem(
                        c.Subcategories.Count == 0
                            ? $"{c.Name}  (whole category — creates “General”)"
                            : $"{c.Name}  (whole category)",
                        $"c:{c.Id}"),
                }
                .Concat(c.Subcategories
                    .OrderBy(s => s.Name)
                    .Select(s => new SelectListItem($"  {c.Name} → {s.Name}", $"s:{s.Id}"))))
            .ToList();

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
