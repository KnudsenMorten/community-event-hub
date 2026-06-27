using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Volunteers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ORGANIZER "Organizer allocation" — the §150 ORGANIZER queue, a mirror of
/// <see cref="BucketAllocationModel"/> (the volunteer queue) for organizer-owned teams
/// (Responsible Team <c>ELDK</c> / <c>ELDK-MOK</c>). It runs the SAME 3-stage lifecycle
/// (availability auto-PROPOSE → lead/organizer validates &amp; QUEUEs → organizer COMMITs)
/// but targets ORGANIZERS: it drives <see cref="OrganizerAllocationService"/> and the
/// shared <see cref="AvailabilityAutoAssignEngine"/> with the Organizer target role, shows
/// the same red/green live coverage simulation, and Commit/Discard/notify identically.
///
/// The queue is SILENT (proposals + edits send nothing); only Commit emails, via
/// <see cref="ICommitNotificationService"/>, batched one summary per affected organizer.
/// Organizer-only, like the volunteer page.
/// </summary>
[Authorize]
public class OrganizerAllocationModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly OrganizerAllocationService _alloc;
    private readonly AvailabilityAutoAssignEngine _engine;
    private readonly ICommitNotificationService _notify;
    private readonly ITaskGuidanceGenerator _guidance;
    private readonly ILogger<OrganizerAllocationModel> _logger;

    public OrganizerAllocationModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        OrganizerAllocationService alloc,
        AvailabilityAutoAssignEngine engine,
        ICommitNotificationService notify,
        ITaskGuidanceGenerator guidance,
        ILogger<OrganizerAllocationModel> logger)
    {
        _db = db;
        _participant = participant;
        _alloc = alloc;
        _engine = engine;
        _notify = notify;
        _guidance = guidance;
        _logger = logger;
    }

    public bool AccessDenied { get; private set; }
    public string? Notice { get; private set; }
    public bool AiEnabled { get; private set; }
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    /// <summary>Tasks grouped by Bucket, each with live coverage (red/green).</summary>
    public List<BucketGroup> Buckets { get; private set; } = new();
    public List<SelectListItem> OrganizerOptions { get; private set; } = new();
    /// <summary>This organizer's current draft queue (pending organizer allocations).</summary>
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

    /// <summary>
    /// §150 STEP 2 — run the availability auto-assign engine for the ORGANIZER queue.
    /// Seeds this organizer's draft queue with <see cref="DraftSource.EngineProposed"/>
    /// rows for organizer-routed tasks. SILENT — no email is sent for a proposal.
    /// </summary>
    public async Task<IActionResult> OnPostRunAutoAssignAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();
        try
        {
            var seeded = await _engine.SeedProposalsAsync(Actor(me), ParticipantRole.Organizer, me.ParticipantId, ct);
            var msg = seeded == 0
                ? "Auto-assign found nothing new to propose (tasks already covered or no matching availability)."
                : $"Auto-assign proposed {seeded} assignment(s) from availability — review the queue below, then Commit.";
            return RedirectToPage(new { Msg = msg });
        }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    public async Task<IActionResult> OnPostAddDraftAsync(int taskId, int organizerId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try { await _alloc.AddDraftAsync(Actor(me), taskId, organizerId, ct); return RedirectToPage(new { Msg = "Added to draft." }); }
        catch (VolunteerValidationException ex) { return RedirectToPage(new { Msg = ex.Message }); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    public async Task<IActionResult> OnPostRemoveDraftAsync(int taskId, int organizerId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try { await _alloc.RemoveDraftAsync(Actor(me), taskId, organizerId, ct); return RedirectToPage(new { Msg = "Removed from draft." }); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    [CommunityHub.Audit.Audit("Committed organizer allocations", Category = CommunityHub.Core.Domain.AuditCategory.Admin, TargetType = "OrganizerAllocation")]
    public async Task<IActionResult> OnPostCommitAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try
        {
            var r = await _alloc.CommitAsync(Actor(me), ct);

            // §150 — COMMIT is the ONLY email path. Notify exactly once per commit, BATCHED
            // one summary per affected organizer; the queue stays SILENT. Notifier respects
            // the ring gate and no-ops on an empty affected set.
            await _notify.NotifyCommitAsync(Actor(me), r.AffectedParticipantIds, ParticipantRole.Organizer, ct);

            var msg = $"Committed {r.Committed} allocation(s).";
            if (r.SkippedDuplicate > 0) msg += $" {r.SkippedDuplicate} already assigned (skipped).";
            if (r.SkippedOutOfRing > 0)
                msg += $" {r.SkippedOutOfRing} left in the queue — those organizers are above the feature's released ring (out of scope). Promote the ring in Settings, then commit again to include them.";
            return RedirectToPage(new { Msg = msg });
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

        OrganizerOptions = await _db.Participants
            .Where(p => p.EventId == me.EventId && p.Role == ParticipantRole.Organizer && p.IsActive)
            .OrderBy(p => p.FullName)
            .Select(p => new SelectListItem(
                p.FullName != "" ? p.FullName : p.Email, p.Id.ToString()))
            .ToListAsync(ct);

        Draft = await _alloc.LoadDraftAsync(actor, ct);
    }
}
