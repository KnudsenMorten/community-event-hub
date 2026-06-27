using System.Text;
using CommunityHub.Auth;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CommunityHub.Api;

/// <summary>
/// Authenticated single-task calendar reminder (REQUIREMENTS §38) — the
/// "Add reminder to calendar" button on each dated task downloads a one-event .ics
/// for the SIGNED-IN participant's own task. The event's UID matches the personal
/// feed's UID for the same task, so downloading then later subscribing to the feed
/// never double-books. Scoped to the caller's own (or their sponsor company's) task.
/// </summary>
[ApiController]
[Authorize]
public sealed class TaskCalendarController : ControllerBase
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ParticipantCalendarBuilder _builder;

    public TaskCalendarController(
        ICurrentParticipantAccessor participant, ParticipantCalendarBuilder builder)
    {
        _participant = participant;
        _builder = builder;
    }

    [HttpGet("/tasks/{taskId:int}/reminder.ics")]
    public async Task<IActionResult> GetTaskReminderAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return Unauthorized();

        var host = Request.Host.Value ?? "communityhub";
        // Builder scopes to the participant's own (or sponsor-company) dated task;
        // null = not theirs or no due date.
        var ics = await _builder.BuildSingleTaskAsync(me.ParticipantId, taskId, host, ct);
        if (ics is null) return NotFound();

        Response.Headers["Cache-Control"] = "no-store, max-age=0";
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", $"task-{taskId}.ics");
    }
}
