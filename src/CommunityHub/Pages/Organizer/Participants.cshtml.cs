using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer participant grid (v2). Builds on the v1 grid + bulk-ops:
///   - filter by status (active/inactive/all), by <b>persona</b>
///     (<see cref="ParticipantRole"/>) and, for sponsors, by <b>sponsor company</b>;
///   - "active" everywhere means the lifecycle-correct
///     <see cref="ParticipantActivation"/> rule (<c>IsActive AND
///     LifecycleState == Active</c>), with active the DEFAULT and a toggle to
///     show inactive / all;
///   - per-row organizer actions: deactivate/reactivate (the cancellation
///     switch), <b>Switch to user</b> (act-as), <b>Modify on behalf</b>, and
///     manage a participant's <b>secure link</b>;
///   - the existing multi-select bulk bar (deactivate / reactivate / change role).
/// Organizer-only and server-enforced (a real Organizer role; an acting-as
/// session can never reach here).
/// </summary>
[Authorize]
public class ParticipantsModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ParticipantBulkOperationService _bulk;
    private readonly ParticipantDeletionService _deletion;
    private readonly ParticipantSearchService _search;
    private readonly ImpersonationAuditService _audit;
    private readonly TimeProvider _clock;

    public ParticipantsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        ParticipantBulkOperationService bulk,
        ParticipantDeletionService deletion,
        ParticipantSearchService search,
        ImpersonationAuditService audit,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _bulk = bulk;
        _deletion = deletion;
        _search = search;
        _audit = audit;
        _clock = clock;
    }

    public List<Participant> Participants { get; private set; } = new();
    public bool AccessDenied { get; private set; }
    public string? ActionMessage { get; private set; }
    public bool ActionIsError { get; private set; }

    /// <summary>Distinct sponsor-company ids present in this edition (for the filter dropdown).</summary>
    public List<string> SponsorCompanyIds { get; private set; } = new();

    /// <summary>Filter: "all", "active", "inactive". Active is lifecycle-correct.</summary>
    [BindProperty(SupportsGet = true)]
    public string ActiveFilter { get; set; } = "active";

    /// <summary>Filter by persona/role, or null for all personas.</summary>
    [BindProperty(SupportsGet = true)]
    public ParticipantRole? RoleFilter { get; set; }

    /// <summary>Filter by sponsor company id (only meaningful for sponsor rows), or null.</summary>
    [BindProperty(SupportsGet = true)]
    public string? SponsorCompanyFilter { get; set; }

    /// <summary>Free-text search over name + email (server-side, case-insensitive).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    /// <summary>Sort column key: name | email | persona | status. Default name.</summary>
    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = "name";

    /// <summary>true = descending. Default false.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Desc { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNo { get; set; } = 1;

    public GridPage Paging { get; private set; }

    public bool NextDescFor(string col) => Sort == col && !Desc;
    public string SortIndicator(string col) => Sort != col ? "" : (Desc ? " ▼" : " ▲");
    public string AriaSort(string col) => Sort != col ? "none" : (Desc ? "descending" : "ascending");

    [BindProperty(SupportsGet = true)]
    public string? Msg { get; set; }

    [BindProperty]
    public List<int> SelectedIds { get; set; } = new();

    [BindProperty]
    public ParticipantRole BulkRole { get; set; }

    /// <summary>Resolve a sponsor company id to its display name (fallback chain).</summary>
    public static string CompanyDisplayName(string companyId) =>
        SponsorCompanyName.Resolve(null, null, null, companyId);

    /// <summary>Lifecycle-correct "is this participant active?" for the status badge.</summary>
    public static bool IsActive(Participant p) => ParticipantActivation.IsActive(p);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me))
        {
            AccessDenied = true;
            return Page();
        }

        if (!string.IsNullOrEmpty(Msg)) ActionMessage = Msg;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Toggle a single participant's IsActive flag (the cancellation switch).</summary>
    public async Task<IActionResult> OnPostToggleActiveAsync(
        int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) return Forbid();

        var target = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == participantId && p.EventId == me.EventId, ct);
        if (target is not null)
        {
            target.IsActive = !target.IsActive;
            await _db.SaveChangesAsync(ct);
        }

        return RedirectToPage(new { ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo });
    }

    /// <summary>
    /// Soft-delete (deactivate) one participant via the deletion service. This is
    /// the safe per-row "Delete" the grid offers by default: the row keeps all its
    /// dependent data but can no longer sign in. Audited.
    /// </summary>
    public async Task<IActionResult> OnPostDeactivateAsync(
        int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) return Forbid();

        var result = await _deletion.DeactivateAsync(me.EventId, participantId, ct);
        var msg = result.Status switch
        {
            ParticipantDeletionService.DeletionStatus.Deactivated =>
                $"{result.FullName} was deactivated and can no longer sign in.",
            ParticipantDeletionService.DeletionStatus.AlreadyInactive =>
                $"{result.FullName} was already inactive.",
            _ => "That participant could not be found in this event.",
        };

        if (result.Found)
        {
            await _audit.RecordAsync(
                me.EventId, ImpersonationActorKind.Organizer,
                actorParticipantId: me.ParticipantId, actorLabel: $"{me.FullName} ({me.Email})",
                targetParticipantId: result.ParticipantId,
                action: ImpersonationAuditService.ActionDeactivate,
                detail: $"Organizer deactivated {result.FullName}.", ct: ct);
        }

        return RedirectToPage(new { ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo, Msg = msg });
    }

    /// <summary>
    /// Per-row Delete. Safe semantics: try a HARD delete (the row was never
    /// engaged — clean its logistics links and remove it); if the participant has
    /// engagement that must not be destroyed, fall back to a SOFT delete
    /// (deactivate) and tell the organizer why. Either outcome is audited so the
    /// removal is never silent.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(
        int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) return Forbid();

        var actorLabel = $"{me.FullName} ({me.Email})";
        var hard = await _deletion.HardDeleteAsync(me.EventId, participantId, ct);

        if (hard.Status == ParticipantDeletionService.DeletionStatus.HardDeleted)
        {
            await _audit.RecordAsync(
                me.EventId, ImpersonationActorKind.Organizer,
                actorParticipantId: me.ParticipantId, actorLabel: actorLabel,
                targetParticipantId: hard.ParticipantId,
                action: ImpersonationAuditService.ActionDelete,
                detail: $"Organizer hard-deleted {hard.FullName} (no dependent data).", ct: ct);
            return RedirectToPage(new
            {
                ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                Msg = $"{hard.FullName} was permanently deleted.",
            });
        }

        if (hard.Status == ParticipantDeletionService.DeletionStatus.NotFound)
        {
            return RedirectToPage(new
            {
                ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                Msg = "That participant could not be found in this event.",
            });
        }

        // Blocked by engagement → safe fallback: deactivate (soft-delete).
        var soft = await _deletion.DeactivateAsync(me.EventId, participantId, ct);
        await _audit.RecordAsync(
            me.EventId, ImpersonationActorKind.Organizer,
            actorParticipantId: me.ParticipantId, actorLabel: actorLabel,
            targetParticipantId: participantId,
            action: ImpersonationAuditService.ActionDeactivate,
            detail: $"Organizer delete fell back to deactivate for {soft.FullName} "
                    + $"(has {string.Join(", ", hard.BlockingDependencies)}).", ct: ct);

        var why = hard.BlockingDependencies.Count > 0
            ? $" (has {string.Join(", ", hard.BlockingDependencies)})"
            : string.Empty;
        return RedirectToPage(new
        {
            ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
            Msg = $"{soft.FullName} has linked data{why}, so they were deactivated "
                  + "instead of permanently deleted.",
        });
    }

    /// <summary>
    /// Start an act-as session: re-issue the cookie as the target participant,
    /// marked organizer-acting-as. Server-enforced organizer-only, and an
    /// already-acting session can never start a nested impersonation.
    /// </summary>
    public async Task<IActionResult> OnPostSwitchToUserAsync(
        int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) return Forbid();

        var target = await _db.Participants.FirstOrDefaultAsync(
            p => p.Id == participantId && p.EventId == me.EventId, ct);
        if (target is null)
        {
            return RedirectToPage(new
            {
                ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                Msg = "That participant could not be found in this event.",
            });
        }
        if (target.Id == me.ParticipantId)
        {
            return RedirectToPage(new
            {
                ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                Msg = "You cannot switch to yourself.",
            });
        }

        var actorLabel = $"{me.FullName} ({me.Email})";
        await ImpersonationSignIn.SignInAsTargetAsync(
            HttpContext, target, ImpersonationActorKind.Organizer,
            actorParticipantId: me.ParticipantId, actorLabel: actorLabel, _clock);

        await _audit.RecordAsync(
            me.EventId, ImpersonationActorKind.Organizer,
            actorParticipantId: me.ParticipantId, actorLabel: actorLabel,
            targetParticipantId: target.Id,
            action: ImpersonationAuditService.ActionStart,
            detail: $"Organizer switched into {target.FullName}'s view.",
            ct: ct);

        // Land on the target's OWN hub home (the role-personalized "My event"
        // view) so the organizer now navigates the WHOLE app as that user —
        // every page, not the 2-field "Modify on behalf" form. This redirect
        // target is the crux of the switch-user fix and is asserted in tests.
        return LocalRedirect(SwitchToUserLandingPath);
    }

    /// <summary>
    /// Where a successful "Switch to user" lands: the hub root, i.e. the target
    /// participant's own role-personalized home — NOT <c>/Organizer/EditOnBehalf</c>.
    /// Exposed so the round-trip test can assert the landing without hard-coding
    /// the literal in two places.
    /// </summary>
    public const string SwitchToUserLandingPath = "/";

    public Task<IActionResult> OnPostBulkDeactivateAsync(CancellationToken ct) =>
        RunBulkAsync((me) => _bulk.DeactivateAsync(me.EventId, SelectedIds, ct),
            verb: "deactivated", ct);

    public Task<IActionResult> OnPostBulkReactivateAsync(CancellationToken ct) =>
        RunBulkAsync((me) => _bulk.ReactivateAsync(me.EventId, SelectedIds, ct),
            verb: "reactivated", ct);

    public Task<IActionResult> OnPostBulkChangeRoleAsync(CancellationToken ct) =>
        RunBulkAsync((me) => _bulk.ChangeRoleAsync(me.EventId, SelectedIds, BulkRole, ct),
            verb: $"moved to {BulkRole}", ct);

    private async Task<IActionResult> RunBulkAsync(
        Func<CurrentParticipant, Task<ParticipantBulkOperationService.BulkResult>> op,
        string verb, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) return Forbid();

        var requested = SelectedIds.Where(id => id > 0).Distinct().Count();
        if (requested == 0)
        {
            return RedirectToPage(new
            {
                ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo,
                Msg = "Pick at least one participant first.",
            });
        }

        var result = await op(me);
        var skipped = result.Skipped(requested);
        var msg = $"{result.Changed} participant(s) {verb}"
            + (result.Matched - result.Changed > 0
                ? $", {result.Matched - result.Changed} already in that state"
                : string.Empty)
            + (skipped > 0 ? $", {skipped} not found" : string.Empty)
            + ".";

        return RedirectToPage(new { ActiveFilter, RoleFilter, SponsorCompanyFilter, Search, Sort, Desc, PageNo, Msg = msg });
    }

    /// <summary>
    /// A real organizer = role Organizer AND not currently acting-as. An
    /// acting-as session (even one impersonating an organizer) must never be
    /// able to drive the organizer grid or start a nested impersonation.
    /// </summary>
    private static bool IsRealOrganizer(CurrentParticipant me) =>
        me.Role == ParticipantRole.Organizer && !me.IsActingAs;

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        // Sponsor-company filter choices: distinct ids on sponsor rows.
        SponsorCompanyIds = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId != null)
            .Select(p => p.SponsorCompanyId!)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct);

        // One authority for filter + sort: ParticipantSearchService. The page only
        // owns the raw query-string binding + paging; the search rules (status,
        // role, sponsor company, free-text, ordering) live in the service so the
        // grid and the global "find a person" box can never drift apart.
        var request = ParticipantSearchService.Parse(
            Search, RoleFilter, persona: null, ActiveFilter, SponsorCompanyFilter, Sort, Desc);
        var query = _search.Query(eventId, request);

        var matched = await query.CountAsync(ct);
        Paging = GridPaging.Resolve(PageNo, GridPaging.DefaultPageSize, matched);

        Participants = await query
            .Skip(Paging.Skip).Take(Paging.PageSize)
            .ToListAsync(ct);
    }
}
