using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Volunteers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Volunteer;

/// <summary>
/// VOLUNTEER unified "My schedule" / "My day" (REQUIREMENTS §20 Volunteer / §21
/// "Volunteer unified My-schedule"): ONE mobile-first page that shows ALL of a
/// volunteer's assigned shifts/tasks time-ordered (where + when), the bucket's
/// supervisor(s) + ELDK lead, an "ask for help" affordance per task, and a
/// personal calendar (subscribe to the per-user feed, or download a single
/// shift's .ics). Booking/event-check-in is NOT here (out of scope — Zoho
/// Backstage); a volunteer can only view their schedule, update their own task
/// progress, and ask for help — all via <see cref="VolunteerStructureService"/>,
/// which enforces that a volunteer touches only tasks they are assigned to.
/// </summary>
[Authorize]
public class MyScheduleModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly VolunteerScheduleBuilder _schedule;
    private readonly VolunteerStructureService _svc;
    private readonly VolunteerShiftService _shifts;
    private readonly VolunteerHelpNotificationService _helpNotify;
    private readonly CalendarInviteEmailService _calendarInvite;
    private readonly ILogger<MyScheduleModel> _logger;

    public MyScheduleModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        VolunteerScheduleBuilder schedule,
        VolunteerStructureService svc,
        VolunteerShiftService shifts,
        VolunteerHelpNotificationService helpNotify,
        CalendarInviteEmailService calendarInvite,
        ILogger<MyScheduleModel> logger)
    {
        _db = db;
        _participant = participant;
        _schedule = schedule;
        _svc = svc;
        _shifts = shifts;
        _helpNotify = helpNotify;
        _calendarInvite = calendarInvite;
        _logger = logger;
    }

    /// <summary>
    /// Statuses a volunteer may set from their own self-service surface. Cancelled
    /// ("No longer needed") is a coordinator/supervisor-only state and is excluded
    /// here so it never appears in — nor is accepted from — the volunteer dropdown.
    /// </summary>
    public static readonly IReadOnlyList<VolunteerTaskStatus> VolunteerSelectableStatuses =
        new[] { VolunteerTaskStatus.Open, VolunteerTaskStatus.InProgress, VolunteerTaskStatus.Done };

    public VolunteerSchedule Schedule { get; private set; } =
        new(Array.Empty<VolunteerScheduleEntry>(), Array.Empty<VolunteerHelpRequest>());

    [TempData] public string? Notice { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await LoadAsync(me, ct);
        return Page();
    }

    private VolunteerStructureService.ActorContext Actor(CurrentParticipant me)
        => new(me.ParticipantId, me.Email, me.Role, me.EventId);

    public async Task<IActionResult> OnPostSetStatusAsync(
        int taskId, VolunteerTaskStatus status, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Defense-in-depth: a volunteer may not set Cancelled ("No longer needed")
        // from their self-service surface — that is a coordinator/supervisor action.
        // Reject it server-side regardless of what the posted dropdown contained.
        if (!VolunteerSelectableStatuses.Contains(status))
        {
            Notice = "You cannot set that status. Ask your supervisor if a task is no longer needed.";
            return RedirectToPage();
        }

        try
        {
            await _svc.SetTaskStatusAsync(Actor(me), taskId, status, ct);
            Notice = "Task updated.";
            return RedirectToPage();
        }
        catch (VolunteerValidationException ex) { Notice = ex.Message; return RedirectToPage(); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    public async Task<IActionResult> OnPostRaiseHelpAsync(
        int taskId, string message, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try
        {
            var req = await _svc.RaiseHelpAsync(Actor(me), taskId, message, ct);
            try { await _helpNotify.NotifySupervisorAsync(me.EventId, req.Id, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Help request {HelpId} saved but supervisor notification failed.", req.Id);
            }
            Notice = "Your supervisor has been asked for help.";
            return RedirectToPage();
        }
        catch (VolunteerValidationException ex) { Notice = ex.Message; return RedirectToPage(); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    // ----- Shift confirm / decline / request-swap (merged from the old MyShifts
    // page, REQUIREMENTS §20). Each decision is stamped via VolunteerShiftService,
    // which resolves the volunteer's OWN assigned shift from the session identity
    // (the participant id is NEVER taken from the client) and raises a
    // coordinator-visible signal on decline / swap-request. -------------------
    public Task<IActionResult> OnPostConfirmAsync(int taskId, CancellationToken ct) =>
        ApplyDecisionAsync(taskId, ShiftDecisionStatus.Confirmed, null, ct);

    public Task<IActionResult> OnPostDeclineAsync(int taskId, string? note, CancellationToken ct) =>
        ApplyDecisionAsync(taskId, ShiftDecisionStatus.Declined, note, ct);

    public Task<IActionResult> OnPostRequestSwapAsync(int taskId, string? note, CancellationToken ct) =>
        ApplyDecisionAsync(taskId, ShiftDecisionStatus.SwapRequested, note, ct);

    public Task<IActionResult> OnPostWithdrawAsync(int taskId, CancellationToken ct) =>
        ApplyDecisionAsync(taskId, ShiftDecisionStatus.None, null, ct);

    private async Task<IActionResult> ApplyDecisionAsync(
        int taskId, ShiftDecisionStatus decision, string? note, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        try
        {
            await _shifts.SetDecisionAsync(me.EventId, me.ParticipantId, taskId, decision, note, ct);
            Notice = decision switch
            {
                ShiftDecisionStatus.Declined => "Shift declined — a coordinator will reassign it.",
                ShiftDecisionStatus.SwapRequested => "Swap requested — a coordinator will pick it up.",
                ShiftDecisionStatus.Confirmed => "Thanks — you're confirmed for this shift.",
                _ => "Shift updated.",
            };
            return RedirectToPage();
        }
        catch (VolunteerValidationException ex) { Notice = ex.Message; return RedirectToPage(); }
        catch (VolunteerAccessDeniedException) { return Forbid(); }
    }

    /// <summary>
    /// §193 "Add Reminder": e-mail the signed-in volunteer a calendar INVITATION
    /// for one assigned task's due date (replacing the old "Download .ics"). Scoped
    /// to the volunteer's own assigned, non-cancelled, dated task; the invite goes
    /// to their chosen calendar / override e-mail. Stable UID so a re-send updates.
    /// </summary>
    public async Task<IActionResult> OnPostAddReminderAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var host = Request.Host.Value ?? "communityhub";
        var assigned = await _db.VolunteerTaskAssignments
            .AsNoTracking()
            .AnyAsync(a => a.EventId == me.EventId
                           && a.ParticipantId == me.ParticipantId
                           && a.TaskId == taskId, ct);
        var task = assigned
            ? await _db.VolunteerTasks
                .AsNoTracking()
                .Where(t => t.Id == taskId
                            && t.EventId == me.EventId
                            && t.DueDate != null
                            && t.Status != VolunteerTaskStatus.Cancelled)
                .Select(t => new { t.Id, t.Title, t.Shift, t.TimeEnd, t.DueDate })
                .FirstOrDefaultAsync(ct)
            : null;

        if (task is null || task.DueDate is null)
        {
            Notice = "That shift could not be found.";
            return RedirectToPage();
        }

        var start = new DateTimeOffset(task.DueDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var when = string.IsNullOrWhiteSpace(task.Shift)
            ? "Volunteer task."
            : string.IsNullOrWhiteSpace(task.TimeEnd)
                ? $"Volunteer task. When: {task.Shift}"
                : $"Volunteer task. When: {task.Shift}-{task.TimeEnd}";

        try
        {
            var sent = await _calendarInvite.SendItemInviteAsync(
                me.ParticipantId,
                uid: $"voltask-{task.Id}@{host}",
                summary: $"Volunteer: {task.Title}",
                description: when,
                location: null,
                start: start,
                end: start.AddDays(1),
                allDay: true,
                fileName: "reminder.ics",
                introHtml: $"Here is a reminder for your volunteer task <strong>{System.Net.WebUtility.HtmlEncode(task.Title)}</strong>.",
                ct: ct);
            Notice = sent
                ? sent.Confirmation("Reminder")
                : "Calendar invitations are turned off for this event.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Add Reminder failed for volunteer task {TaskId}", taskId);
            Notice = "We couldn't send that reminder just now — please try again.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        Schedule = await _schedule.BuildAsync(me.EventId, me.ParticipantId, ct);
    }
}
