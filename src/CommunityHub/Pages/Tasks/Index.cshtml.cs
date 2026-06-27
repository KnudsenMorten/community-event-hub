using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Tasks;

/// <summary>
/// The participant's task list (CONTEXT.md section 9). Shows tasks assigned
/// to the signed-in participant for the active edition and lets them mark a
/// task done / not done.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly CommunityHub.Core.Participants.FormTaskReconciler _reconciler;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        CommunityHub.Core.Participants.FormTaskReconciler reconciler)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _reconciler = reconciler;
    }

    public List<ParticipantTask> Tasks { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // §147: the page renders ONE unified task list (shared _TaskListPanel) — no
        // longer the shared _ChecklistCard too — so tasks appear once. We still run the
        // reconciler here (the side-effect the old checklist build provided) so tasks
        // whose form data is already submitted show as Done. Idempotent; no-op when
        // nothing changed.
        await _reconciler.ReconcileAsync(me.EventId, me.ParticipantId, ct);

        Tasks = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && t.AssignedParticipantId == me.ParticipantId)
            .OrderBy(t => t.State)
            .ThenBy(t => t.DueDate)
            .ToListAsync(ct);
        return Page();
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
        }
        return RedirectToPage();
    }
}
