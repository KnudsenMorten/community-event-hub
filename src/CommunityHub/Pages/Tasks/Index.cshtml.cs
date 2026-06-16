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
    private readonly CommunityHub.Core.Participants.ParticipantChecklistBuilder _checklist;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        CommunityHub.Core.Participants.ParticipantChecklistBuilder checklist)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _checklist = checklist;
    }

    public List<ParticipantTask> Tasks { get; private set; } = new();

    /// <summary>
    /// The unified "what's still needed" checklist (REQUIREMENTS Top-8 #7) — the
    /// SAME shared component the Hub home and attendee My-event render, so the
    /// Tasks page no longer competes as a separate landing surface. Built by the
    /// shared <see cref="CommunityHub.Core.Participants.ParticipantChecklistBuilder"/>.
    /// </summary>
    public CommunityHub.Core.Participants.ParticipantChecklist Checklist { get; private set; } =
        new(System.Array.Empty<CommunityHub.Core.Participants.ChecklistRow>(),
            System.Array.Empty<CommunityHub.Core.Participants.ChecklistRow>());

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Checklist = await _checklist.BuildAsync(me.EventId, me.ParticipantId, ct);

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
