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
/// ORGANIZER "Allocation scenarios" (REQUIREMENTS §129) — the generic stage → simulate →
/// commit workspace over <see cref="AllocationScenarioService"/>. An organizer creates a named
/// scenario (or auto-seeds a backfill from a drop-out), stages people→task moves, sees a live
/// READ-ONLY simulation (coverage gaps / capacity breaches / double-booking conflicts / a
/// before-after diff), then COMMITS it atomically (with an over-capacity acknowledgement gate) or
/// DISCARDS it. Organizer-only. Nothing is written to the live assignment tables until commit.
/// </summary>
[Authorize]
public class AllocationScenariosModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly AllocationScenarioService _scenarios;

    public AllocationScenariosModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant,
        AllocationScenarioService scenarios)
    {
        _db = db;
        _participant = participant;
        _scenarios = scenarios;
    }

    public bool AccessDenied { get; private set; }
    public string? Notice { get; private set; }
    public string? Error { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }
    [BindProperty(SupportsGet = true)] public string? Err { get; set; }

    [BindProperty(SupportsGet = true)] public int? Id { get; set; }

    // Create form
    [BindProperty] public string? NewTitle { get; set; }
    [BindProperty] public AllocationScenarioKind NewKind { get; set; } = AllocationScenarioKind.VolunteerAllocation;
    // Add-move form
    [BindProperty] public int MoveTaskId { get; set; }
    [BindProperty] public int MovePersonId { get; set; }
    [BindProperty] public AllocationMoveOp MoveOp { get; set; } = AllocationMoveOp.Assign;
    // Drop-out seed
    [BindProperty] public int DroppedPersonId { get; set; }
    // Commit
    [BindProperty] public bool AckOverCapacity { get; set; }

    public List<AllocationScenario> Scenarios { get; private set; } = new();
    public AllocationScenario? Selected { get; private set; }
    public AllocationScenarioService.SimulationResult? Sim { get; private set; }

    public List<SelectListItem> TaskOptions { get; private set; } = new();
    public List<SelectListItem> PersonOptions { get; private set; } = new();
    public int MyParticipantId { get; private set; }

    private VolunteerStructureService.ActorContext Actor(CurrentParticipant me)
        => new(me.ParticipantId, me.Email, me.Role, me.EventId);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Notice = Msg;
        Error = Err;
        await LoadAsync(me, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        try
        {
            var id = await _scenarios.CreateAsync(Actor(me), NewKind, NewTitle ?? "Untitled scenario", ct: ct);
            return RedirectToPage(new { id, Msg = "Scenario created — stage your moves, then simulate." });
        }
        catch (Exception ex) when (ex is VolunteerValidationException or VolunteerAccessDeniedException) { return RedirectToPage(new { Err = ex.Message }); }
    }

    public async Task<IActionResult> OnPostSeedBackfillAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        try
        {
            var id = await _scenarios.SeedDropOutBackfillAsync(Actor(me), DroppedPersonId, ct);
            return id is null
                ? RedirectToPage(new { Err = "That person covers no tasks — nothing to backfill." })
                : RedirectToPage(new { id, Msg = "Backfill seeded from the drop-out's uncovered tasks." });
        }
        catch (Exception ex) when (ex is VolunteerValidationException or VolunteerAccessDeniedException) { return RedirectToPage(new { Err = ex.Message }); }
    }

    public async Task<IActionResult> OnPostAddMoveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        try
        {
            var role = await _db.Participants.Where(p => p.Id == MovePersonId)
                .Select(p => p.Role).FirstOrDefaultAsync(ct);
            var targetRole = role == ParticipantRole.Organizer ? ParticipantRole.Organizer : ParticipantRole.Volunteer;
            await _scenarios.AddTaskMoveAsync(Actor(me), Id!.Value, MovePersonId, MoveTaskId, targetRole, MoveOp, ct);
            return RedirectToPage(new { id = Id, Msg = "Move staged." });
        }
        catch (Exception ex) when (ex is VolunteerValidationException or VolunteerAccessDeniedException) { return RedirectToPage(new { id = Id, Err = ex.Message }); }
    }

    /// <summary>"Assign me" convenience — stage the signed-in organizer onto the chosen task.</summary>
    public async Task<IActionResult> OnPostAssignMeAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        try
        {
            await _scenarios.AddTaskMoveAsync(Actor(me), Id!.Value, me.ParticipantId, MoveTaskId,
                ParticipantRole.Organizer, AllocationMoveOp.Assign, ct);
            return RedirectToPage(new { id = Id, Msg = "Staged you onto the task." });
        }
        catch (Exception ex) when (ex is VolunteerValidationException or VolunteerAccessDeniedException) { return RedirectToPage(new { id = Id, Err = ex.Message }); }
    }

    public async Task<IActionResult> OnPostRemoveMoveAsync(int moveId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        try { await _scenarios.RemoveMoveAsync(Actor(me), Id!.Value, moveId, ct); }
        catch (Exception ex) when (ex is VolunteerValidationException or VolunteerAccessDeniedException) { return RedirectToPage(new { id = Id, Err = ex.Message }); }
        return RedirectToPage(new { id = Id, Msg = "Move removed." });
    }

    public async Task<IActionResult> OnPostCommitAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        try
        {
            var r = await _scenarios.CommitAsync(Actor(me), Id!.Value, AckOverCapacity, ct);
            return RedirectToPage(new { id = Id,
                Msg = $"Committed: {r.Assigned} assigned, {r.Unassigned} unassigned, {r.Skipped} already-in-place." });
        }
        catch (Exception ex) when (ex is VolunteerValidationException or VolunteerAccessDeniedException) { return RedirectToPage(new { id = Id, Err = ex.Message }); }
    }

    public async Task<IActionResult> OnPostDiscardAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }
        try { await _scenarios.DiscardAsync(Actor(me), Id!.Value, ct); }
        catch (Exception ex) when (ex is VolunteerValidationException or VolunteerAccessDeniedException) { return RedirectToPage(new { id = Id, Err = ex.Message }); }
        return RedirectToPage(new { Msg = "Scenario discarded." });
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        MyParticipantId = me.ParticipantId;
        Scenarios = await _scenarios.ListAsync(Actor(me), ct);

        if (Id is int sid)
        {
            Selected = await _scenarios.GetWithMovesAsync(Actor(me), sid, ct);
            if (Selected is not null)
            {
                Sim = await _scenarios.SimulateAsync(Actor(me), sid, ct);

                TaskOptions = await _db.VolunteerTasks
                    .Where(t => t.EventId == me.EventId)
                    .OrderBy(t => t.Title)
                    .Select(t => new SelectListItem(
                        $"{t.Title} (needs {t.ResourcesNeeded})", t.Id.ToString()))
                    .ToListAsync(ct);

                PersonOptions = await _db.Participants
                    .Where(p => p.EventId == me.EventId && p.IsActive
                                && (p.Role == ParticipantRole.Volunteer || p.Role == ParticipantRole.Organizer))
                    .OrderBy(p => p.FullName)
                    .Select(p => new SelectListItem($"{p.FullName} ({p.Role})", p.Id.ToString()))
                    .ToListAsync(ct);
            }
        }
    }
}
