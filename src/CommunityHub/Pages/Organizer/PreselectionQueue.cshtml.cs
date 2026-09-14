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

    /// <summary>§1146 — the availability grid rendered under the list.</summary>
    private readonly CommunityHub.Core.Volunteers.VolunteerAvailabilityOverviewService _availability;

    public PreselectionQueueModel(
        ICurrentParticipantAccessor participant,
        PreselectionQueueService queue,
        ParticipantActivationService activation,
        ParticipantDeletionService deletion,
        CommunityHub.Core.Volunteers.VolunteerAvailabilityOverviewService availability)
    {
        _participant = participant;
        _queue = queue;
        _activation = activation;
        _deletion = deletion;
        _availability = availability;
    }

    /// <summary>
    /// §1146 — the green/red half-day grid for the people in the queue above.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-28: <i>"primarely to preselect people based on their availability"</i>. It is
    /// built from THE ROWS THIS PAGE IS SHOWING, so the grid can never describe a different
    /// population from the table it sits under — including when the source filter is applied.
    /// </remarks>
    public CommunityHub.Core.Volunteers.VolunteerAvailabilityOverviewService.Overview? Availability
    { get; private set; }

    /// <summary>
    /// §1146b — who consented to being featured publicly (the sign-up's "Public profile" step).
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-28: <i>"include email + phone + Some publish accept (yes/no) in this
    /// form"</i>.</para>
    ///
    /// <para>🔒 <b>ABSENT is NO.</b> The consent lives on <c>VolunteerAvailability.ProfileConsent</c>,
    /// which defaults false and only exists once a volunteer has been through the form — so a
    /// missing row must read as "no", never as blank. Publishing someone's name and photo because a
    /// row was missing is not a recoverable mistake.</para>
    /// </remarks>
    public IReadOnlyDictionary<int, CommunityHub.Core.Volunteers.VolunteerAvailabilityOverviewService.SignupDetails>
        SignupById { get; private set; } =
        new Dictionary<int, CommunityHub.Core.Volunteers.VolunteerAvailabilityOverviewService.SignupDetails>();

    /// <summary>
    /// §1146c — the ring picked per row (operator 2026-08-28: <i>"ring selection button + current
    /// ring + their id"</i>).
    /// </summary>
    /// <remarks>
    /// 🔑 Indexed by participant id so every row can carry its own value inside the ONE bulk form
    /// the table already lives in. A nested per-row form would be invalid HTML, and a single shared
    /// field name would make every row post the last row's choice.
    /// </remarks>
    [BindProperty]
    public Dictionary<int, CommunityHub.Core.Settings.Ring> RingByParticipant { get; set; } = new();

    public IReadOnlyList<Participant> Queue { get; private set; } = new List<Participant>();
    public bool AccessDenied { get; private set; }
    public string? ActionMessage { get; private set; }

    // ⚰️ §1146h — the SourceFilter property is gone with its dropdown (operator 2026-08-28:
    // "remove the source filter too"). §756 made this queue volunteers-only, so it could only ever
    // filter everything or nothing. `PreselectionQueueService.GetQueueAsync` still takes the
    // optional source — that is a tested capability with other callers; this PAGE has nothing to
    // filter, so it no longer carries the parameter through every redirect.

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
        Queue = await _queue.GetQueueAsync(me.EventId, source: null, ct);
        var queueIds = Queue.Select(q => q.Id).ToList();
        Availability = await _availability.BuildAsync(me.EventId, queueIds, ct);
        SignupById = await _availability.SignupDetailsAsync(me.EventId, queueIds, ct);
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
            ? $"Participant onboarded/confirmed, {result.OnboardingEmailsSent} welcome email(s) sent."
            : "No change (already at or beyond that state).";
        return RedirectToPage(new { Msg = msg });
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
            return RedirectToPage(new { Msg = "Pick at least one row first." });
        }

        var result = await _activation.ActivateAndOnboardAsync(me.EventId, SelectedIds, ct);
        var q = result.Queue;
        var skipped = q.Skipped(requested);
        // ⚰️ §880.4 — the "N speaker(s) NOT activated" sentence was here and is GONE, for the reason
        // given on the single-row handler above: this queue is volunteers only (§756), so the
        // refusal it described cannot occur here. The service-side gate is untouched.
        var alreadyThere = q.Matched - q.Changed;
        var msg = $"{q.Changed} row(s) onboarded/confirmed"
            + (alreadyThere > 0 ? $", {alreadyThere} already there" : string.Empty)
            + (skipped > 0 ? $", {skipped} not found" : string.Empty)
            + $", {result.OnboardingEmailsSent} welcome email(s) sent.";
        return RedirectToPage(new { Msg = msg });
    }

    /// <summary>
    /// Delete a queue row (REQUIREMENTS §21 organizer "PreselectionQueue delete
    /// dupes/spam"). Queue rows are prospective Inactive/Preselected participants
    /// who have not yet engaged, so this reuses the shared
    /// <see cref="ParticipantDeletionService"/>: a clean row is hard-deleted; a row
    /// that somehow has dependent data falls back to deactivate (safe semantics,
    /// never orphans links). Organizer-only, edition-scoped.
    /// </summary>
    /// <summary>
    /// §1146c — set one queue row's release RING (operator 2026-08-28).
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Why this belongs on THIS page.</b> The ring is what decides whether onboarding
    /// mail actually reaches the person, and the welcome is sent by the Onboard/Confirmed button
    /// three columns to the right. Making him leave the queue to set it — and come back — is how a
    /// volunteer gets confirmed at the wrong ring.</para>
    ///
    /// <para>🔒 Event-scoped: a participant id from another edition is not found and nothing is
    /// written, matching every other mutation on this page.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostSetRingAsync(int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!RingByParticipant.TryGetValue(participantId, out var ring))
        {
            return RedirectToPage(new { Msg = "No ring was chosen for that row." });
        }

        var person = await _queue.FindInEventAsync(me.EventId, participantId, ct);
        if (person is null)
        {
            return RedirectToPage(new { Msg = "That row could not be found in this edition." });
        }

        var before = person.Ring;
        if (before == ring)
        {
            return RedirectToPage(new
            {

                Msg = $"{person.FullName} is already at {CommunityHub.Core.Settings.Rings.Label(ring)}.",
            });
        }

        await _queue.SetRingAsync(me.EventId, participantId, ring, ct);

        return RedirectToPage(new
        {

            Msg = $"{person.FullName}: ring changed from {CommunityHub.Core.Settings.Rings.Label(before)} "
                + $"to {CommunityHub.Core.Settings.Rings.Label(ring)}.",
        });
    }

    /// <summary>
    /// §1146d — undo a preselection without touching the sign-up (operator 2026-08-28).
    /// </summary>
    public async Task<IActionResult> OnPostResetAsync(int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var person = await _queue.FindInEventAsync(me.EventId, participantId, ct);
        var outcome = await _queue.ResetAsync(me.EventId, participantId, ct);

        var who = person?.FullName ?? "That row";
        var msg = outcome switch
        {
            PreselectionQueueService.ResetOutcome.Reset =>
                $"{who} is back to Inactive — their sign-up, availability, photo and consent are untouched.",
            PreselectionQueueService.ResetOutcome.NoChange =>
                $"{who} was already Inactive.",
            // 🔒 Say WHY, and name the action that does apply. A bare refusal on a button he just
            // pressed reads as a bug.
            PreselectionQueueService.ResetOutcome.RefusedActive =>
                $"{who} is already onboarded and can sign in, so a reset is refused — "
                + "use the deactivate button to remove them.",
            _ => "That row could not be found in this edition.",
        };

        return RedirectToPage(new { Msg = msg });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int participantId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        // §253 G15: the dependents cleanup at the DbContext seam removes the old FK
        // holes (PartyRsvp / volunteer availability), but any FUTURE Restrict FK not
        // yet probed must degrade to the safe fallback (deactivate), never a 500.
        // 🔴 §1082 — DEACTIVATE, NEVER HARD-DELETE (operator 2026-08-13: *"remove the delete buttons
        // for organizers, so they can only deactive"*). This used to hard-delete a "clean" queue row
        // and fall back to deactivation only when a Restrict FK refused — so whether a person's rows
        // survived depended on which dependencies they happened to have. Now it is one answer for
        // everybody, and the try/catch that existed to keep a failed delete off a 500 goes with it.
        var soft = await _deletion.DeactivateAsync(me.EventId, participantId, ct);
        var msg = soft.Status == ParticipantDeletionService.DeletionStatus.NotFound
            ? "That row could not be found in this edition."
            : $"{soft.FullName}'s sign-up is now hidden — it left the queue and cannot sign in. Nothing was deleted; the row and all their answers are kept.";
        return RedirectToPage(new { Msg = msg });
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
        return RedirectToPage(new { Msg = msg });
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
            return RedirectToPage(new { Msg = "Pick at least one row first." });
        }

        var result = await op(me);
        var skipped = result.Skipped(requested);
        var msg = $"{result.Changed} row(s) {verb}"
            + (result.Matched - result.Changed > 0
                ? $", {result.Matched - result.Changed} already there"
                : string.Empty)
            + (skipped > 0 ? $", {skipped} not found" : string.Empty)
            + ".";
        return RedirectToPage(new { Msg = msg });
    }
}
