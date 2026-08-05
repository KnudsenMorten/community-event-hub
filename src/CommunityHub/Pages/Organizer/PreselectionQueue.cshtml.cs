using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer view of the pre-selection queue — the holding area where
/// prospective volunteers / speakers / media-team land (from the Sessionize-API
/// speaker sync and the volunteer interest form) as Inactive / Preselected.
/// The organizer validates the data, then advances rows along the lifecycle
/// <c>Inactive → Preselected → Active</c>, single (per-row button) OR
/// multi-select (select-all + bulk advance). The mutation runs in
/// <see cref="PreselectionQueueService"/> (event-scoped, forward-only,
/// idempotent); only an Active row can sign in.
/// </summary>
[Authorize]
public class PreselectionQueueModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly PreselectionQueueService _queue;
    private readonly ParticipantActivationService _activation;
    private readonly ParticipantDeletionService _deletion;

    public PreselectionQueueModel(
        ICurrentParticipantAccessor participant,
        PreselectionQueueService queue,
        ParticipantActivationService activation,
        ParticipantDeletionService deletion)
    {
        _participant = participant;
        _queue = queue;
        _activation = activation;
        _deletion = deletion;
    }

    public IReadOnlyList<Participant> Queue { get; private set; } = new List<Participant>();
    public bool AccessDenied { get; private set; }
    public string? ActionMessage { get; private set; }

    /// <summary>Filter by inbound source, or null for all.</summary>
    [BindProperty(SupportsGet = true)]
    public ParticipantQueueSource? SourceFilter { get; set; }

    /// <summary>Carries a result banner across the post-redirect-get.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Msg { get; set; }

    /// <summary>Ticked rows for a bulk action.</summary>
    [BindProperty]
    public List<int> SelectedIds { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (!string.IsNullOrEmpty(Msg)) ActionMessage = Msg;
        Queue = await _queue.GetQueueAsync(me.EventId, SourceFilter, ct);
        return Page();
    }

    /// <summary>Advance one row to Preselected.</summary>
    public Task<IActionResult> OnPostPreselectOneAsync(int participantId, CancellationToken ct) =>
        AdvanceOneAsync(participantId, ParticipantLifecycleState.Preselected, "preselected", ct);

    /// <summary>Activate one row (flips IsActive on) AND auto-sends the persona
    /// onboarding email set, no approval (10a-1).</summary>
    public async Task<IActionResult> OnPostActivateOneAsync(int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var result = await _activation.ActivateAndOnboardAsync(
            me.EventId, new[] { participantId }, ct);

        // ⚰️ §880.4 — the "uncategorized SPEAKER refused" branch was here and is GONE. It could not
        // fire: `PreselectionQueueService.QueueRows` filters `Role == Volunteer` (§756), so every id
        // this page can submit belongs to a volunteer, and a volunteer has no speaker category to
        // be missing. Its only real effect was to tell the next reader that speakers land in this
        // queue. They do not — they have their own door at /Organizer/PendingSpeakers.
        //
        // 🔒 THE GATE ITSELF IS UNTOUCHED. `ParticipantActivationService` still refuses an
        // uncategorized speaker and still reports it in `RefusedUncategorizedSpeakerIds`
        // (§299 6.1, covered directly by PreselectionQueueServiceTests). What was removed is one
        // page's rendering of an outcome that page cannot produce.
        var msg = result.Queue.Changed == 1
            ? $"Participant activated, {result.OnboardingEmailsSent} onboarding email(s) sent."
            : "No change (already at or beyond that state).";
        return RedirectToPage(new { SourceFilter, Msg = msg });
    }

    /// <summary>Bulk-advance every ticked row to Preselected.</summary>
    public Task<IActionResult> OnPostBulkPreselectAsync(CancellationToken ct) =>
        RunBulkAsync(me => _queue.PreselectAsync(me.EventId, SelectedIds, ct), "preselected", ct);

    /// <summary>Bulk-activate every ticked row (flips IsActive on) + auto-onboard.</summary>
    public async Task<IActionResult> OnPostBulkActivateAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var requested = SelectedIds.Where(id => id > 0).Distinct().Count();
        if (requested == 0)
        {
            return RedirectToPage(new { SourceFilter, Msg = "Pick at least one row first." });
        }

        var result = await _activation.ActivateAndOnboardAsync(me.EventId, SelectedIds, ct);
        var q = result.Queue;
        var skipped = q.Skipped(requested);
        // ⚰️ §880.4 — the "N speaker(s) NOT activated" sentence was here and is GONE, for the reason
        // given on the single-row handler above: this queue is volunteers only (§756), so the
        // refusal it described cannot occur here. The service-side gate is untouched.
        var alreadyThere = q.Matched - q.Changed;
        var msg = $"{q.Changed} row(s) activated"
            + (alreadyThere > 0 ? $", {alreadyThere} already there" : string.Empty)
            + (skipped > 0 ? $", {skipped} not found" : string.Empty)
            + $", {result.OnboardingEmailsSent} onboarding email(s) sent.";
        return RedirectToPage(new { SourceFilter, Msg = msg });
    }

    /// <summary>
    /// Delete a queue row (REQUIREMENTS §21 organizer "PreselectionQueue delete
    /// dupes/spam"). Queue rows are prospective Inactive/Preselected participants
    /// who have not yet engaged, so this reuses the shared
    /// <see cref="ParticipantDeletionService"/>: a clean row is hard-deleted; a row
    /// that somehow has dependent data falls back to deactivate (safe semantics,
    /// never orphans links). Organizer-only, edition-scoped.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        // §253 G15: the dependents cleanup at the DbContext seam removes the old FK
        // holes (PartyRsvp / volunteer availability), but any FUTURE Restrict FK not
        // yet probed must degrade to the safe fallback (deactivate), never a 500.
        string msg;
        try
        {
            var hard = await _deletion.HardDeleteAsync(me.EventId, participantId, ct);
            msg = hard.Status switch
            {
                ParticipantDeletionService.DeletionStatus.HardDeleted =>
                    $"{hard.FullName} was removed from the queue.",
                ParticipantDeletionService.DeletionStatus.NotFound =>
                    "That row could not be found in this edition.",
                _ => await DeactivateFallbackAsync(me.EventId, participantId, hard, ct),
            };
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            var soft = await _deletion.DeactivateAsync(me.EventId, participantId, ct);
            msg = $"{soft.FullName} has linked data, so they were deactivated "
                  + "instead of permanently removed.";
        }
        return RedirectToPage(new { SourceFilter, Msg = msg });
    }

    private async Task<string> DeactivateFallbackAsync(
        int eventId, int participantId,
        ParticipantDeletionService.DeletionResult hard, CancellationToken ct)
    {
        var soft = await _deletion.DeactivateAsync(eventId, participantId, ct);
        var why = hard.BlockingDependencies.Count > 0
            ? $" (has {string.Join(", ", hard.BlockingDependencies)})"
            : string.Empty;
        return $"{soft.FullName} has linked data{why}, so they were deactivated "
               + "instead of permanently removed.";
    }

    private async Task<IActionResult> AdvanceOneAsync(
        int participantId, ParticipantLifecycleState target, string verb, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var result = await _queue.AdvanceAsync(me.EventId, new[] { participantId }, target, ct);
        var msg = result.Changed == 1
            ? $"Participant {verb}."
            : "No change (already at or beyond that state).";
        return RedirectToPage(new { SourceFilter, Msg = msg });
    }

    private async Task<IActionResult> RunBulkAsync(
        Func<CurrentParticipant, Task<PreselectionQueueService.QueueResult>> op,
        string verb, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var requested = SelectedIds.Where(id => id > 0).Distinct().Count();
        if (requested == 0)
        {
            return RedirectToPage(new { SourceFilter, Msg = "Pick at least one row first." });
        }

        var result = await op(me);
        var skipped = result.Skipped(requested);
        var msg = $"{result.Changed} row(s) {verb}"
            + (result.Matched - result.Changed > 0
                ? $", {result.Matched - result.Changed} already there"
                : string.Empty)
            + (skipped > 0 ? $", {skipped} not found" : string.Empty)
            + ".";
        return RedirectToPage(new { SourceFilter, Msg = msg });
    }
}
