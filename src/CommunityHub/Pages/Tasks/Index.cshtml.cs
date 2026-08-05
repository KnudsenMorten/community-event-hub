using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Tasks;

/// <summary>
/// The participant's task list (CONTEXT.md section 9). Shows tasks assigned
/// to the signed-in participant for the active edition and lets them mark a
/// task done / not done.
///
/// <para>🔒 <b>§708.10 — THIS PAGE IS NOW A WIZARD-STEP HOST.</b> Each row whose task names a
/// <see cref="ParticipantTask.FormStepKey"/> renders that step's REAL form inline, through the same
/// <see cref="IWizardStepHandler"/> the Get-Started wizard uses. Operator 2026-07-30:
/// <i>"yes go with shared tasks page with forms embedded"</i>.</para>
/// </summary>
/// <remarks>
/// <para>🔑 <b>Why a host and not a link.</b> The row already carried an <i>"Open the … form"</i>
/// button (the seeder appends a markdown link to every description and <c>TaskTextLinkifier</c>
/// renders it), so it linked OUT to the form instead of holding it — a FOURTH DOOR in the §708.2
/// sense. §708.2a had already decided this case: <i>"where the form is EMBEDDED in the task, there is
/// no link at all — the form is simply there."</i></para>
///
/// <para>🔒 <b>The handlers are REUSED, never reimplemented</b> (§708.1 / §708.2a: one service, one
/// fields partial, N hosts). This page is the third host after <c>/Forms/Wizard</c> and the standalone
/// <c>/Forms/*</c> pages, so validation, entitlement gating and save behaviour cannot diverge between
/// where a form is embedded and where it is linked. A form that behaved differently in a task row
/// would be a fourth door in the §708.2 sense.</para>
/// </remarks>
[Authorize]
public class IndexModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly CommunityHub.Core.Participants.FormTaskReconciler _reconciler;
    private readonly CommunityHub.Forms.WizardStepTaskSeeder _wizardStepTasks;
    private readonly CommunityHub.Core.Config.PartyTaskSeeder _partyTasks;
    private readonly Dictionary<string, IWizardStepHandler> _handlers;
    private readonly CommunityHub.Core.Tasks.TaskBodyService? _taskBodies;
    private readonly ILogger<IndexModel>? _log;
    private readonly CommunityHub.Core.Audit.IAuditTrail? _audit;   // §728

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        CommunityHub.Core.Participants.FormTaskReconciler reconciler,
        CommunityHub.Forms.WizardStepTaskSeeder wizardStepTasks,
        CommunityHub.Core.Config.PartyTaskSeeder partyTasks,
        IEnumerable<IWizardStepHandler> handlers,
        CommunityHub.Core.Tasks.TaskBodyService? taskBodies = null,
        ILogger<IndexModel>? log = null,
        // §728 — optional + last so existing constructions keep compiling.
        CommunityHub.Core.Audit.IAuditTrail? audit = null)
    {
        _audit = audit;
        _taskBodies = taskBodies;
        _db = db;
        _participant = participant;
        _clock = clock;
        _reconciler = reconciler;
        _wizardStepTasks = wizardStepTasks;
        _partyTasks = partyTasks;
        _log = log;

        // Keyed exactly as the wizard keys them, so both hosts resolve the same handler for a step.
        _handlers = new(StringComparer.Ordinal);
        foreach (var h in handlers) _handlers[h.Key] = h;
    }

    public List<ParticipantTask> Tasks { get; private set; } = new();

    /// <summary>
    /// The loaded step handler per TASK ID — what each row embeds. Keyed by task id, not by step
    /// key, so a row can never render another row's form.
    /// </summary>
    public IReadOnlyDictionary<int, IWizardStepHandler> RowForms => _rowForms;
    private readonly Dictionary<int, IWizardStepHandler> _rowForms = new();

    /// <summary>Set when an embedded form was just saved, so the row can confirm it.</summary>
    [TempData] public string? SavedMessage { get; set; }

    /// <summary>The task id whose embedded form failed validation and must re-render with errors.</summary>
    public int? InvalidTaskId { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // §173e: seed any missing Get-Started step-tasks (+ the party task) so this list
        // MIRRORS the role's Get-Started journey, then reconcile. Both idempotent; tolerate
        // a seed hiccup so the list still renders.
        // 🔴 §708.10c — TOLERATE THE HICCUP, BUT NEVER SWALLOW IT SILENTLY. A bare `catch { }` here
        // hid a seeder that was throwing on EVERY load for a participant with a duplicate SourceKey,
        // which is why §708.10's embedded forms never appeared and why nothing anywhere said so.
        try { await _partyTasks.EnsureForParticipantAsync(me.EventId, me.ParticipantId, me.Role, ct); }
        catch (Exception ex) { _log?.LogError(ex, "Party task seed failed for participant {Pid}.", me.ParticipantId); }
        try { await _wizardStepTasks.EnsureForParticipantAsync(me.EventId, me.ParticipantId, me.Role, ct); }
        catch (Exception ex) { _log?.LogError(ex, "Wizard step task seed failed for participant {Pid}.", me.ParticipantId); }

        // §147: the page renders ONE unified task list (shared _TaskListPanel) — no
        // longer the shared _ChecklistCard too — so tasks appear once. We still run the
        // reconciler here (the side-effect the old checklist build provided) so tasks
        // whose form data is already submitted show as Done. Idempotent; no-op when
        // nothing changed.
        await _reconciler.ReconcileAsync(me.EventId, me.ParticipantId, ct);

        await LoadTasksAsync(me, ct);
        await LoadRowFormsAsync(me, ct);
        RenderedBodies = await RenderMigratedBodiesAsync(ct);
        return Page();
    }

    /// <summary>
    /// §708.11 — every MIGRATED row's authored body, by task id. Empty for unmigrated rows, which
    /// keep rendering their stored <c>Description</c> exactly as before (§684.21's regression gate).
    /// </summary>
    public IReadOnlyDictionary<int, string> RenderedBodies { get; private set; }
        = new Dictionary<int, string>();

    /// <remarks>
    /// 🔒 <b>Fail-soft PER TASK</b> — §682. One body that throws is left out of the map and that row
    /// falls back to its stored prose; the other seven still render. A participant seeing one plain
    /// task beats a participant seeing an error page.
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, string>> RenderMigratedBodiesAsync(CancellationToken ct)
    {
        if (_taskBodies is null) return new Dictionary<int, string>();

        var rendered = new Dictionary<int, string>();
        foreach (var task in Tasks)
        {
            if (!_taskBodies.IsMigrated(task)) continue;
            try
            {
                var result = await _taskBodies.RenderAsync(
                    task, CommunityHub.Core.Tasks.TaskBodyFlavour.Html, ct: ct);
                if (result is not null && !string.IsNullOrWhiteSpace(result.Html))
                    rendered[task.Id] = result.Html;
            }
            catch (Exception ex)
            {
                _log?.LogError(
                    ex, "Task body render failed for task {TaskId}; it falls back to its stored "
                    + "description.", task.Id);
            }
        }
        return rendered;
    }

    /// <summary>
    /// §708.10 — SAVE one row's embedded form, through the step's own handler.
    /// </summary>
    /// <remarks>
    /// 🔒 The step key comes from the TASK ROW, re-read from the database, never from the post. A
    /// posted step key would let anyone drive any handler from any row; re-reading it means the row
    /// you saved is the row you were shown. The task id is scoped to the signed-in participant by
    /// the same query, so it cannot address someone else's task either.
    /// </remarks>
    public async Task<IActionResult> OnPostStepAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var task = await _db.Tasks.AsNoTracking().FirstOrDefaultAsync(
            t => t.Id == taskId
                 && t.EventId == me.EventId
                 && t.AssignedParticipantId == me.ParticipantId, ct);

        var handler = task?.FormStepKey is { Length: > 0 } key ? Resolve(key) : null;
        if (task is null || handler is null) return RedirectToPage();

        var outcome = await handler.SaveAsync(BuildContext(me, ct));

        if (outcome == WizardStepOutcome.Invalid)
        {
            // Re-render THIS page with the posted values + ModelState errors on that row, rather
            // than bouncing to the wizard — being bounced out of the list is what embedding the
            // form was meant to stop.
            InvalidTaskId = taskId;
            await LoadTasksAsync(me, ct);
            await LoadRowFormsAsync(me, ct);
            _rowForms[taskId] = handler;   // already holds the posted values from SaveAsync
            return Page();
        }

        // The reconciler closes the task from the data the save just wrote, so the badge is right
        // on the redirect rather than one load behind (§708's "the row shows the truth now" rule).
        try { await _reconciler.ReconcileAsync(me.EventId, me.ParticipantId, ct); } catch { }

        SavedMessage = "Saved.";
        return RedirectToPage(null, null, new { }, ParticipantAnchor(taskId));
    }

    /// <summary>Toggle a task between Done and Open.</summary>
    public async Task<IActionResult> OnPostToggleAsync(
        int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.Id == taskId
                 && t.EventId == me.EventId
                 && t.AssignedParticipantId == me.ParticipantId,
            ct);

        if (task is not null)
        {
            var completing = task.State != TaskState.Done;
            if (task.State == TaskState.Done)
            {
                task.State = TaskState.Open;
                task.CompletedAt = null;
            }
            else
            {
                task.State = TaskState.Done;
                task.CompletedAt = _clock.GetUtcNow();
            }
            await _db.SaveChangesAsync(ct);

            // 🔒 §728 — name WHICH task and WHICH direction. The auto-capture recorded this as
            // `POST /Tasks [Toggle]` — you could see that a task changed, but never which one or
            // whether it was ticked or un-ticked, which is most of what an organizer wants to know
            // when asking "did they do it?".
            if (_audit is not null)
            {
                await _audit.RecordAsync(new CommunityHub.Core.Domain.AuditEntry
                {
                    EventId = me.EventId,
                    OccurredUtc = _clock.GetUtcNow(),
                    Category = CommunityHub.Core.Domain.AuditCategory.UserAction,
                    Action = completing
                        ? CommunityHub.Core.Audit.AuditActions.TaskComplete
                        : CommunityHub.Core.Audit.AuditActions.TaskReopen,
                    ActorParticipantId = me.ParticipantId,
                    ActorEmail = me.Email,
                    ActorRole = me.Role.ToString(),
                    TargetType = "Task",
                    TargetId = task.Id.ToString(),
                    Summary = completing
                        ? $"Marked task done: “{task.Title}”"
                        : $"Re-opened task: “{task.Title}”",
                    Outcome = CommunityHub.Core.Domain.AuditOutcome.Success,
                    Source = CommunityHub.Core.Domain.AuditSource.Web,
                    HttpMethod = "POST",
                    Path = Request.Path.Value,
                }, ct);

                // Set after the write — see AuditPageFilter.SuppressGenericKey.
                HttpContext.Items[CommunityHub.Audit.AuditPageFilter.SuppressGenericKey] = true;
            }
        }
        return RedirectToPage();
    }

    private async Task LoadTasksAsync(CurrentParticipant me, CancellationToken ct)
    {
        Tasks = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && t.AssignedParticipantId == me.ParticipantId)
            .OrderBy(t => t.State)
            .ThenBy(t => t.DueDate)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Load the embedded form for every row that names a step this hub can render inline.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Fail-soft PER ROW</b> — §682's standing lesson, and the same rule §708 applied to task
    /// bodies: one step whose load throws must not take the task list down with it. That row simply
    /// renders without its form (title, body and the link-out it already had); the others still work.
    /// </remarks>
    private async Task LoadRowFormsAsync(CurrentParticipant me, CancellationToken ct)
    {
        foreach (var task in Tasks)
        {
            if (task.FormStepKey is not { Length: > 0 } key) continue;
            if (Resolve(key) is not { } handler) continue;

            try
            {
                await handler.LoadAsync(BuildContext(me, ct));
                _rowForms[task.Id] = handler;
            }
            catch (Exception ex)
            {
                _log?.LogError(
                    ex, "Embedded step form '{StepKey}' failed to load for task {TaskId}; "
                    + "the row renders without it.", key, task.Id);
            }
        }
    }

    private IWizardStepHandler? Resolve(string key) => _handlers.GetValueOrDefault(key);

    private WizardStepContext BuildContext(CurrentParticipant me, CancellationToken ct) =>
        new(me.EventId, me.ParticipantId, me.Role, me.Email, me.FullName, this,
            model => TryUpdateModelAsync(model, model.GetType(), name: string.Empty),
            ct);

    /// <summary>The fragment for one row — the SAME shape Home links to (§708.4).</summary>
    private static string ParticipantAnchor(int taskId) =>
        CommunityHub.Core.Participants.ParticipantChecklistBuilder.TaskAnchor(taskId).TrimStart('#');
}
