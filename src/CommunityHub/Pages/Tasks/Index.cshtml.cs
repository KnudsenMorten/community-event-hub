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

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
    }

    public List<ParticipantTask> Tasks { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

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
