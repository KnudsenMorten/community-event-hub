using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;

namespace CommunityHub.Pages.Shared;

/// <summary>
/// §147: the model for the shared <c>_TaskListPanel</c> partial — the ONE unified
/// task-list rendering used by all three task pages (volunteer/media/eventpartner
/// <c>/Tasks</c>, <c>/Speaker/Tasks</c>, <c>/Sponsor/Tasks</c>). It carries the task
/// list plus the per-role row partial to render each task with, so the list STRUCTURE
/// (completion % header → pending at the top → completed collapsed at the bottom) and
/// the completion maths are identical for every role while a role can still vary its
/// own row (sponsor adds the Optional badge + an .ics link).
/// </summary>
public sealed class TaskListPanelView
{
    /// <summary>All of the participant's tasks (any order — the panel splits + sorts them).</summary>
    public required IReadOnlyList<ParticipantTask> Tasks { get; init; }

    /// <summary>
    /// Name of the per-row partial to render each task (e.g. <c>_SpeakerTaskRow</c>,
    /// <c>_SponsorTaskRow</c>, <c>_ParticipantTaskRow</c>). Resolved relative to the
    /// host page's folder (then Shared), so each page supplies a name that lives next
    /// to it.
    /// </summary>
    public required string RowPartialName { get; init; }

    /// <summary>
    /// Pending (not-done) tasks, ordered for display: overdue first, then by due date,
    /// then title — the SAME order the shared checklist card uses.
    /// </summary>
    public IReadOnlyList<ParticipantTask> Pending
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            return Tasks
                .Where(t => t.State != TaskState.Done)
                .OrderByDescending(t => t.DueDate is DateOnly d && d < today) // overdue first
                .ThenBy(t => t.DueDate ?? DateOnly.MaxValue)
                .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Completed tasks, ordered by most-recently-completed then title.</summary>
    public IReadOnlyList<ParticipantTask> Completed =>
        Tasks
            .Where(t => t.State == TaskState.Done)
            .OrderByDescending(t => t.CompletedAt ?? DateTimeOffset.MinValue)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>"X of N done" + percent, computed from <see cref="Tasks"/>.</summary>
    public TaskCompletion Completion => TaskCompletion.From(Tasks);
}
