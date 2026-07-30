using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// Speaker "My tasks" (operator 2026-06-23) — the speaker deadline tasks rendered
/// in the sponsor-style collapse/expand list. Tasks are the config-seeded
/// <c>speakerdl:</c> <see cref="ParticipantTask"/> rows (one set per speaker);
/// completion is per-speaker via <see cref="SpeakerMilestoneService.ToggleAsync"/>.
/// Action + upload buttons are authored as [label](url) markdown in each task's
/// description (rendered by TaskTextLinkifier), so this page needs no per-task
/// upload widget.
/// </summary>
[Authorize]
public class TasksModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerDeadlineSeeder _seeder;
    private readonly CommunityHub.Forms.WizardStepTaskSeeder _wizardStepTasks;
    private readonly CommunityHub.Core.Config.PartyTaskSeeder _partyTasks;
    private readonly FormTaskReconciler _formTaskReconciler;
    private readonly SpeakerMilestoneService _milestones;
    private readonly SpeakerReadinessService _readiness;
    private readonly CommunityHub.Core.Email.CalendarInviteEmailService? _calendarInvite;
    private readonly ILogger<TasksModel>? _log;

    /// <summary>§660 — the edition's organizer inbox, rendered as a mailto link in the instructions.</summary>
    public string OrganizerEmail { get; }

    public TasksModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SpeakerDeadlineSeeder seeder,
        CommunityHub.Forms.WizardStepTaskSeeder wizardStepTasks,
        CommunityHub.Core.Config.PartyTaskSeeder partyTasks,
        FormTaskReconciler formTaskReconciler,
        SpeakerMilestoneService milestones,
        SpeakerReadinessService readiness,
        // §315: optional + last so existing unit tests need not construct the invite
        // service; DI always injects it in production (same pattern as WizardModel §285).
        CommunityHub.Core.Email.CalendarInviteEmailService? calendarInvite = null,
        ILogger<TasksModel>? log = null,
        // §660: optional for the same reason — the organizer address is display-only, and a test
        // that does not supply the config still renders the shipped fallback rather than blank.
        EventEditionConfigLoader? cfg = null,
        EventConfigOptions? cfgOptions = null)
    {
        _db = db;
        _participant = participant;
        _seeder = seeder;
        _wizardStepTasks = wizardStepTasks;
        _partyTasks = partyTasks;
        _formTaskReconciler = formTaskReconciler;
        _milestones = milestones;
        _readiness = readiness;
        _calendarInvite = calendarInvite;
        _log = log;

        // §660 — the "contact the organizers" mailto on this page.
        OrganizerEmail = cfg is not null && cfgOptions is not null
            ? CommunityHub.Content.OrganizerContact.Resolve(cfg, cfgOptions)
            : CommunityHub.Content.OrganizerContact.Fallback;
    }

    public bool NotSpeaker { get; private set; }
    public List<ParticipantTask> Tasks { get; private set; } = new();

    /// <summary>
    /// §138: the speaker's "am I ready?" readiness rollup (score + the what's-missing
    /// list), surfaced at the TOP of My Tasks now that the standalone /Speaker/Readiness
    /// nav item is removed. A pure read-only AGGREGATE of existing data via
    /// <see cref="SpeakerReadinessService"/>; null when the speaker has no
    /// <see cref="SpeakerProfile"/> yet (the view then omits the rollup).
    /// </summary>
    public SpeakerReadiness? Readiness { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { NotSpeaker = true; return Page(); }

        // Ensure this speaker's deadline tasks exist (idempotent), then load them.
        try { await _seeder.SeedAsync(me.EventId, ct); } catch { /* tolerate seed hiccup */ }

        // §173e: ensure My-Tasks MIRRORS the Get-Started journey — seed a task for every
        // Get-Started step (Speaker details / Signal / Code of Conduct, plus the party
        // task; §313/§314: Calendar + Promote no longer have step tasks) so they list
        // here alongside the deadline tasks. Idempotent.
        try { await _partyTasks.EnsureForParticipantAsync(me.EventId, me.ParticipantId, me.Role, ct); } catch { }
        try { await _wizardStepTasks.EnsureForParticipantAsync(me.EventId, me.ParticipantId, me.Role, ct); } catch { }

        // Mark any OPEN logistics deadline tasks Done where the speaker has already
        // submitted the matching form data (hotel/dinner/lunch/swag/travel), and sync the
        // §173e step tasks both ways, so the list reflects real completion. Idempotent.
        await _formTaskReconciler.ReconcileAsync(me.EventId, me.ParticipantId, ct);

        // The deadline tasks (speakerdl:) PLUS the §173e Get-Started step tasks. The
        // logistics FORM tasks (hotel-form: …) are intentionally NOT included for speakers —
        // their speakerdl deadline already represents the same step, so this avoids a dupe.
        Tasks = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && t.AssignedParticipantId == me.ParticipantId
                        && t.SourceKey != null
                        && (t.SourceKey.StartsWith(SpeakerMilestoneService.SourceKeyPrefix)
                            || t.SourceKey.StartsWith(CommunityHub.Core.Participants.WizardStepTaskKeys.SpeakerDetailsPrefix)
                            || t.SourceKey.StartsWith(CommunityHub.Core.Participants.WizardStepTaskKeys.AcceptPrefix)
                            || t.SourceKey.StartsWith("signal:")
                            || t.SourceKey.StartsWith(CommunityHub.Core.Config.PartyTaskSeeder.PartyTaskKey + ":")))
            .OrderBy(t => t.State)
            .ThenBy(t => t.DueDate)
            .ThenBy(t => t.Title)
            .ToListAsync(ct);

        // §138: build the readiness rollup AFTER seeding + reconcile, so the score
        // reflects already-submitted form data on first visit. Read-only; null when the
        // speaker has no SpeakerProfile (the view omits the rollup card).
        Readiness = await _readiness.BuildForSpeakerAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    /// <summary>Toggle one of the speaker's own tasks done/open (per-speaker scoped).</summary>
    public async Task<IActionResult> OnPostToggleAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        await _milestones.ToggleAsync(me.EventId, me.ParticipantId, taskId, ct);
        return RedirectToPage();
    }

    /// <summary>
    /// §315 (operator 2026-07-24) "Add reminder to calendar" on My Tasks too — the same
    /// §193b invite the Home pending-tasks table offers: e-mail the speaker a calendar
    /// INVITATION for one of their own dated tasks (stable UID ⇒ re-click updates the
    /// same entry; honors the calendar/override e-mail). Fail-soft with a flash message.
    /// </summary>
    public async Task<IActionResult> OnPostAddReminderAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (_calendarInvite is null) return RedirectToPage();

        var host = Request.Host.Value ?? "communityhub";
        var task = await _db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId
                        && t.EventId == me.EventId
                        && t.DueDate != null
                        && t.AssignedParticipantId == me.ParticipantId)
            .Select(t => new { t.Id, t.Title, t.Description, t.DueDate })
            .FirstOrDefaultAsync(ct);
        if (task is null || task.DueDate is null)
        {
            TempData["CalendarInviteMessage"] = "That task could not be found.";
            return RedirectToPage();
        }

        var start = new DateTimeOffset(task.DueDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var description = string.IsNullOrWhiteSpace(task.Description)
            ? "Deadline from your Event Hub. Open the hub to update this item."
            : CommunityHub.Core.Email.TaskMarkup.ToPlainText(task.Description);
        try
        {
            var sent = await _calendarInvite.SendItemInviteAsync(
                me.ParticipantId,
                uid: $"task-{task.Id}@{host}",
                summary: task.Title,
                description: description,
                location: null,
                start: start,
                end: start.AddDays(1),
                allDay: true,
                fileName: "reminder.ics",
                introHtml: $"Here is a reminder for <strong>{System.Net.WebUtility.HtmlEncode(task.Title)}</strong>, due {task.DueDate.Value:d MMM yyyy}.",
                ct: ct);
            TempData["CalendarInviteMessage"] = sent
                ? sent.Confirmation("Reminder")
                : "Calendar invitations are turned off for this event.";
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Add Reminder failed for task {TaskId}", taskId);
            TempData["CalendarInviteMessage"] = "We couldn't send that reminder just now — please try again.";
        }
        return RedirectToPage();
    }

    /// <summary>§321: one click — one calendar invitation per dated OPEN task of this
    /// speaker (same per-task UIDs as the single button, so re-clicks update, never
    /// duplicate). Fail-soft per task; reports the sent count.</summary>
    public async Task<IActionResult> OnPostAddReminderAllAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (_calendarInvite is null) return RedirectToPage();

        var host = Request.Host.Value ?? "communityhub";
        var tasks = await _db.Tasks
            .AsNoTracking()
            .Where(t => t.EventId == me.EventId
                        && t.AssignedParticipantId == me.ParticipantId
                        && t.DueDate != null
                        && t.State != TaskState.Done)
            .OrderBy(t => t.DueDate)
            .Select(t => new { t.Id, t.Title, t.Description, t.DueDate })
            .ToListAsync(ct);
        if (tasks.Count == 0)
        {
            TempData["CalendarInviteMessage"] = "No dated pending tasks to send reminders for.";
            return RedirectToPage();
        }

        var sent = 0;
        var invitesOff = false;
        foreach (var task in tasks)
        {
            var start = new DateTimeOffset(task.DueDate!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var description = string.IsNullOrWhiteSpace(task.Description)
                ? "Deadline from your Event Hub. Open the hub to update this item."
                : CommunityHub.Core.Email.TaskMarkup.ToPlainText(task.Description);
            try
            {
                var ok = await _calendarInvite.SendItemInviteAsync(
                    me.ParticipantId,
                    uid: $"task-{task.Id}@{host}",
                    summary: task.Title,
                    description: description,
                    location: null,
                    start: start,
                    end: start.AddDays(1),
                    allDay: true,
                    fileName: "reminder.ics",
                    introHtml: $"Here is a reminder for <strong>{System.Net.WebUtility.HtmlEncode(task.Title)}</strong>, due {task.DueDate.Value:d MMM yyyy}.",
                    ct: ct);
                if (ok) sent++; else invitesOff = true;
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Add-all reminders: send failed for task {TaskId}", task.Id);
            }
        }

        TempData["CalendarInviteMessage"] = sent == 0
            ? (invitesOff
                ? "Calendar invitations are turned off for this event."
                : "We couldn't send the reminders just now — please try again.")
            : $"Sent {sent} calendar invitation{(sent == 1 ? "" : "s")} — one per pending task. Check your inbox.";
        return RedirectToPage();
    }
}
