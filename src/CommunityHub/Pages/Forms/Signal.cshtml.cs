using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// "Join Signal groups" Get-Started step (REQUIREMENTS §109). Renders the
/// role-appropriate Signal chat + broadcast invite buttons (from
/// config/signal-groups.&lt;edition&gt;.json) and a MANUAL "mark completed" control —
/// joining is external, so completion is a manual mark-done, exactly like the upload
/// tasks. Completion is tracked on a per-participant <c>signal:</c>
/// <see cref="ParticipantTask"/> so it also surfaces in the participant's task list /
/// reminders. Only roles in scope per the config see the step (Speakers, Volunteers,
/// Event Partners get chat+broadcast; Media gets broadcast only).
/// </summary>
[Authorize]
public class SignalModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SignalGroupsProvider _signal;
    private readonly TimeProvider _clock;

    public SignalModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SignalGroupsProvider signal,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _signal = signal;
        _clock = clock;
    }

    public bool OutOfScope { get; private set; }
    public SignalGroupLinks? Links { get; private set; }
    public bool Done { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Links = _signal.GetForRole(me.Role);
        if (Links is null) { OutOfScope = true; return Page(); }

        // Ensure the "Join Signal groups" task exists (idempotent) so it also shows in
        // the participant's task list + reminders (§109 "AND a task"), then read state.
        var task = await EnsureTaskAsync(me.EventId, me.ParticipantId, ct);
        Done = task.State == TaskState.Done;
        return Page();
    }

    /// <summary>Toggle the manual "Join Signal groups" completion (mark done / not done).</summary>
    public async Task<IActionResult> OnPostToggleAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (_signal.GetForRole(me.Role) is null) return RedirectToPage();

        var task = await EnsureTaskAsync(me.EventId, me.ParticipantId, ct);
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
        return RedirectToPage();
    }

    private async Task<ParticipantTask> EnsureTaskAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = WizardStepTasks.Signal(participantId);
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null)
        {
            task = new ParticipantTask
            {
                EventId = eventId,
                AssignedParticipantId = participantId,
                Title = "Join Signal groups",
                Description = "Join the ELDK27 Signal chat + broadcast group, then mark this done.",
                State = TaskState.Open,
                IsMandatory = false,
                SourceKey = sourceKey,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync(ct);
        }
        return task;
    }
}
