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

    // §708 step 2 — the migrated-task body pipeline. Both are null-safe for an unmigrated row: the
    // service answers null and the row falls back to its stored Description, byte for byte.
    private readonly CommunityHub.Core.Tasks.TaskBodyService? _taskBodies;
    private readonly CommunityHub.Core.Tasks.SpeakerTaskPlaceholderBuilder? _placeholders;

    // §708 / §707.57d — the two upload tasks. The reconciler derives their state from the decks
    // that actually exist; the service backs the upload control embedded in the task body.
    private readonly CommunityHub.Core.Tasks.SpeakerPresentationTaskReconciler? _presentationTasks;
    private readonly CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService? _presentations;

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
        EventConfigOptions? cfgOptions = null,
        // §708 step 2: optional + last, same pattern as the three above, so the existing test
        // construction sites compile unchanged. DI always supplies both in production.
        CommunityHub.Core.Tasks.TaskBodyService? taskBodies = null,
        CommunityHub.Core.Tasks.SpeakerTaskPlaceholderBuilder? placeholders = null,
        CommunityHub.Core.Tasks.SpeakerPresentationTaskReconciler? presentationTasks = null,
        CommunityHub.Core.Integrations.Graphics.SpeakerPresentationService? presentations = null)
    {
        _taskBodies = taskBodies;
        _placeholders = placeholders;
        _presentationTasks = presentationTasks;
        _presentations = presentations;
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

    /// <summary>
    /// §708 step 2 — the RENDERED HTML body of every MIGRATED task on this page, by task id. A task
    /// absent from this map is not migrated and its row renders its stored <c>Description</c>
    /// exactly as before.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>This had to ship BEFORE the first speaker definition existed, and that ordering is
    /// the whole reason it is its own step.</b> A registry-backed row stores NO prose (§684.14 —
    /// every migrated sponsor row in PROD carries <c>DescLen = 0</c>). Land the definitions first and
    /// this page would render each speaker task as a title and a due date with the BODY SILENTLY
    /// GONE — no error, no failing test, a green build and eight empty tasks. §707.43 and §707.54a
    /// were both exactly this: a silent omission in this same code.</para>
    ///
    /// <para>Rendered in the page model rather than the row partial for the sponsor page's reason
    /// (§684.9): a body's <c>:::data</c> directives resolve asynchronously, so doing it once per page
    /// keeps the renderers synchronous.</para>
    /// </remarks>
    public IReadOnlyDictionary<int, string> RenderedBodies { get; private set; }
        = new Dictionary<int, string>();

    /// <summary>
    /// §708 — the artefact KINDS ("preview" / "final") this speaker has satisfied for EVERY one of
    /// their sessions. The row derives its badge and its note from this rather than from a tick.
    /// </summary>
    public IReadOnlySet<string> UploadedKinds { get; private set; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// §707.57d — this speaker's sessions and each deck's current file, for the upload control
    /// embedded in the two presentation tasks. Empty when the speaker has no sessions.
    /// </summary>
    public IReadOnlyList<CommunityHub.Core.Integrations.Graphics.PresentationSessionSlot> SessionSlots
    { get; private set; }
        = Array.Empty<CommunityHub.Core.Integrations.Graphics.PresentationSessionSlot>();

    /// <summary>Set after an upload POST so the view can confirm or explain.</summary>
    [TempData] public string? UploadMessage { get; set; }

    /// <summary>Set after a FAILED upload POST.</summary>
    [TempData] public string? UploadError { get; set; }

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

        // §708 / §707.57d — derive the two upload tasks' state from the decks BEFORE rendering, so
        // the row shows the truth on this page load rather than one load behind. Fail-soft (§682):
        // a SharePoint problem must not stop the task list rendering, and the reconciler itself
        // declines to change anything it could not verify.
        if (_presentationTasks is not null)
        {
            try
            {
                var state = await _presentationTasks.ReadStateAsync(me.EventId, me.ParticipantId, ct);
                await _presentationTasks.ReconcileAsync(Tasks, state, ct);
                UploadedKinds = state.Satisfied;
            }
            catch (Exception ex)
            {
                _log?.LogError(
                    ex, "Presentation-backed task reconciliation failed for speaker {ParticipantId}.",
                    me.ParticipantId);
            }
        }

        if (_presentations is not null)
        {
            try
            {
                SessionSlots = await _presentations.GetSessionSlotsAsync(
                    me.EventId, me.ParticipantId, ct);
            }
            catch (Exception ex)
            {
                _log?.LogError(
                    ex, "Could not load session slots for the embedded upload control "
                    + "(speaker {ParticipantId}).", me.ParticipantId);
            }
        }

        // §708 step 2 — render the migrated bodies for this page.
        RenderedBodies = await RenderMigratedBodiesAsync(ct);

        // §138: build the readiness rollup AFTER seeding + reconcile, so the score
        // reflects already-submitted form data on first visit. Read-only; null when the
        // speaker has no SpeakerProfile (the view omits the rollup card).
        Readiness = await _readiness.BuildForSpeakerAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    /// <summary>
    /// §708 step 2 — render every MIGRATED task's body for this page.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Fail-soft, PER TASK</b> — §682 is the standing lesson: one bad task body took the whole
    /// sponsor order sync down. A task whose body throws is simply left out of the map, so it falls
    /// back to its stored <c>Description</c> and the OTHER seven still render. A speaker seeing one
    /// plain task beats a speaker seeing an error page.
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, string>> RenderMigratedBodiesAsync(
        CancellationToken ct)
    {
        if (_taskBodies is null || _placeholders is null) return new Dictionary<int, string>();

        var migrated = Tasks.Where(t => _taskBodies.IsMigrated(t)).ToList();
        if (migrated.Count == 0) return new Dictionary<int, string>();

        var placeholders = _placeholders.Build();
        var rendered = new Dictionary<int, string>();

        foreach (var task in migrated)
        {
            try
            {
                var result = await _taskBodies.RenderAsync(
                    task, CommunityHub.Core.Tasks.TaskBodyFlavour.Html, placeholders, ct: ct);

                if (result is null || string.IsNullOrWhiteSpace(result.Html)) continue;

                rendered[task.Id] = result.Html;
                // 🔒 An unresolved placeholder renders as EMPTY and is invisible on the page; this
                // log is the only signal that a task quietly lost its form button (§688.4).
                _placeholders.ReportMissing(result.Definition.Key, result.Missing);
            }
            catch (Exception ex)
            {
                _log?.LogError(
                    ex, "Could not render the migrated body for speaker task {TaskId} ('{Title}'); "
                    + "falling back to the stored description.", task.Id, task.Title);
            }
        }

        return rendered;
    }

    /// <summary>Toggle one of the speaker's own tasks done/open (per-speaker scoped).</summary>
    public async Task<IActionResult> OnPostToggleAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // 🔒 §708 / §707.57d — AN ARTEFACT-BACKED TASK CANNOT BE COMPLETED BY HAND. SERVER-SIDE.
        //
        // The row hides the button, but hiding a control is not enforcing a rule: the POST is still
        // reachable from a page kept open from before this change, or by replaying the form. This is
        // the exact defect he photographed — "Upload final presentation ✓ done" with no file — and
        // it survived because the only thing standing between a speaker and a false Done was a
        // button. Its state follows the deck and nothing else. Mirrors the sponsor page's guard.
        var task = await _db.Tasks.AsNoTracking().FirstOrDefaultAsync(
            t => t.Id == taskId
                 && t.EventId == me.EventId
                 && t.AssignedParticipantId == me.ParticipantId, ct);

        if (task is not null
            && !CommunityHub.Core.Organizer.TaskArtefactRules.AllowsManualCompletion(task))
        {
            TempData["UploadMessage"] =
                "This task completes automatically when your presentation is uploaded — "
                + "there is nothing to tick off by hand.";
            return RedirectToPage();
        }

        await _milestones.ToggleAsync(me.EventId, me.ParticipantId, taskId, ct);
        return RedirectToPage();
    }

    /// <summary>
    /// §707.57d — receive a session's deck FROM INSIDE THE TASK.
    /// </summary>
    /// <remarks>
    /// <para>This is the conversion he asked for: <i>"the control is EMBEDDED IN THE TASK, not a
    /// link out to another page"</i>. Deliberately the SAME service <c>/Speaker</c> uses — the §455
    /// streaming path, the §68 version numbering and the §428 own-session check are shared, not
    /// re-implemented, so the two entry points cannot diverge on what an upload means.</para>
    ///
    /// <para>Completion is NOT set here. It is DERIVED from the deck by
    /// <see cref="CommunityHub.Core.Tasks.SpeakerPresentationTaskReconciler"/> (the service also
    /// closes the task on upload), so the task and the file can never disagree — which is the whole
    /// defect being removed.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostUploadPresentationAsync(
        string kind, int sessionId, IFormFile? file, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) return RedirectToPage();
        if (_presentations is null) return RedirectToPage();

        var presentationKind = string.Equals(kind, "final", StringComparison.OrdinalIgnoreCase)
            ? CommunityHub.Core.Integrations.Graphics.PresentationKind.Final
            : CommunityHub.Core.Integrations.Graphics.PresentationKind.Preview;

        if (file is null || file.Length == 0)
        {
            UploadError = "Choose a PDF, PPTX or ZIP file first.";
            return RedirectToPage();
        }
        if (!_presentations.CanUpload(presentationKind))
        {
            UploadError = "Uploads aren't configured yet — please contact the organizers.";
            return RedirectToPage();
        }

        // §461/§456 — a SPONSOR-category speaker owns the FINAL deck only. The body's control is
        // hidden for them, and hiding is not a gate: the handler refuses it too. Same reasoning as
        // the identical check on /Speaker, and the same lesson as §299 7.1.
        if (presentationKind == CommunityHub.Core.Integrations.Graphics.PresentationKind.Preview
            && await _db.SpeakerProfiles.AnyAsync(
                sp => sp.EventId == me.EventId && sp.ParticipantId == me.ParticipantId
                      && sp.Category == SpeakerCategory.Sponsor, ct))
        {
            UploadError = "Only the final presentation is required for your session.";
            return RedirectToPage();
        }

        try
        {
            // §455 — stream straight into Graph's chunked upload session; a 1 GB deck never lands
            // in memory. The buffered overload exists for callers that already hold bytes.
            await using var upload = file.OpenReadStream();
            var stored = await _presentations.UploadStreamAsync(
                me.EventId, me.ParticipantId, sessionId, presentationKind,
                file.FileName, upload, file.Length, ct);
            UploadMessage =
                $"✅ Uploaded {stored} — a re-upload becomes the next version automatically, "
                + "and the newest version is what attendees see.";
        }
        catch (InvalidOperationException ex)
        {
            UploadError = ex.Message;   // wrong file type / not your session
        }
        catch (Exception ex)
        {
            _log?.LogWarning(
                ex, "Task-embedded slide upload failed for participant {Pid} (session {SessionId}, {Kind}).",
                me.ParticipantId, sessionId, kind);
            UploadError = "We couldn't store that file just now — please try again.";
        }

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
