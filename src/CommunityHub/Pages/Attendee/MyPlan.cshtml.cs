using CommunityHub.Auth;
using CommunityHub.Core.Attendees;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Attendee;

/// <summary>
/// The attendee self-service "My plan" page: a personal running order the
/// participant builds themselves by saving the talks they want to attend across
/// the whole public agenda. Distinct from the reconciled Master Class shown on
/// My Event (which comes from their Zoho booking) — this is their own free choice,
/// a private bookmark list that never books a seat (booking stays in Zoho Bookings).
///
/// Two halves on one mobile-first page: the saved talks (with a one-tap Remove),
/// and a "browse to add" list of the edition's sessions with a Save / Saved toggle.
/// Own-row scoped + server-enforced in <see cref="AttendeePlanService"/> — a
/// participant can only ever see and change their OWN plan. Any signed-in role may
/// keep a plan; it is most useful for attendees but harmless for everyone.
/// </summary>
[Authorize]
public class MyPlanModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly AttendeePlanService _plan;
    private readonly PublicSessionsService _sessions;

    public MyPlanModel(
        ICurrentParticipantAccessor participant,
        AttendeePlanService plan,
        PublicSessionsService sessions)
    {
        _participant = participant;
        _plan = plan;
        _sessions = sessions;
    }

    /// <summary>The participant's saved talks, ordered scheduled-first.</summary>
    public AttendeePlan Plan { get; private set; } = new();

    /// <summary>The active edition's public sessions to browse + add (may be empty).</summary>
    public PublicSessionsView? Browse { get; private set; }

    /// <summary>The set of session ids currently in the plan (drives the Save/Saved toggle).</summary>
    public IReadOnlySet<int> SavedIds { get; private set; } = new HashSet<int>();

    /// <summary>True when at least one saved talk has a confirmed time (the .ics download is offered).</summary>
    public bool HasScheduled { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await LoadAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    /// <summary>
    /// Toggle a session in the plan (Save when not saved, Remove when saved).
    /// Server re-validates that the session belongs to the participant's own
    /// edition, so a stale page cannot save a foreign id. Idempotent.
    /// </summary>
    public async Task<IActionResult> OnPostToggleAsync(int sessionId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await _plan.ToggleAsync(me.EventId, me.ParticipantId, sessionId, ct);
        return RedirectToPage();
    }

    /// <summary>Remove a session from the plan (the saved-list Remove button). Idempotent.</summary>
    public async Task<IActionResult> OnPostRemoveAsync(int sessionId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await _plan.RemoveAsync(me.EventId, me.ParticipantId, sessionId, ct);
        return RedirectToPage();
    }

    /// <summary>
    /// Download the participant's OWN scheduled saved talks as a calendar file
    /// (GET ?handler=Ics). Authenticated + own-row scoped via the signed-in
    /// participant — a participant can only ever export their own plan. Returns a
    /// valid RFC 5545 <c>text/calendar</c> attachment; 404 when there is nothing
    /// scheduled to add (the page still shows the saved list, this is only the
    /// calendar export). No-store so a re-download always reflects the live plan.
    /// </summary>
    public async Task<IActionResult> OnGetIcsAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var host = Request.Host.Value ?? "communityhub";
        var ics = await _plan.BuildPlanIcsAsync(
            me.EventId, me.ParticipantId, me.FullName, me.Email, host, ct);
        if (ics is null) return NotFound();

        Response.Headers["Cache-Control"] = "no-store, max-age=0";
        return File(
            System.Text.Encoding.UTF8.GetBytes(ics),
            "text/calendar; charset=utf-8",
            "my-plan.ics");
    }

    private async Task LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        Plan = await _plan.BuildPlanAsync(eventId, participantId, ct);
        Browse = await _sessions.BuildAsync(ct: ct);
        SavedIds = await _plan.GetSavedSessionIdsAsync(eventId, participantId, ct);
        HasScheduled = Plan.ScheduledCount > 0;
    }
}
