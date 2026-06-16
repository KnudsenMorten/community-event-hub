using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// One speaker milestone, as shown on the Speaker Hub progress tracker.
/// Built from a speaker's existing deadline <see cref="ParticipantTask"/>
/// rows (the ones the <c>SpeakerDeadlineSeeder</c> creates with a
/// <c>speakerdl:</c> SourceKey). This is a read-model -- it does not create
/// or mutate tasks; it only derives display state (countdown, status) so the
/// hub can render a cohesive speaker journey.
/// </summary>
public sealed record SpeakerMilestone(
    int TaskId,
    string Title,
    string? Description,
    DateOnly? DueDate,
    bool Done,
    DateTimeOffset? CompletedAt)
{
    /// <summary>
    /// Whole days from "today" until the due date. Negative = overdue,
    /// 0 = due today, positive = days remaining. Null when there is no due
    /// date or the milestone is already done.
    /// </summary>
    public int? DaysUntilDue { get; init; }

    /// <summary>True when not done and the due date is in the past.</summary>
    public bool Overdue => !Done && DaysUntilDue is < 0;

    /// <summary>True when not done and the due date is today.</summary>
    public bool DueToday => !Done && DaysUntilDue == 0;

    /// <summary>How many days overdue (>=1) for display; null otherwise.</summary>
    public int? DaysOverdue => Overdue ? -DaysUntilDue : null;
}

/// <summary>
/// The full speaker-journey progress view for one speaker.
/// </summary>
public sealed record SpeakerMilestoneProgress(
    IReadOnlyList<SpeakerMilestone> Milestones)
{
    public int Total => Milestones.Count;
    public int DoneCount => Milestones.Count(m => m.Done);
    public int OpenCount => Total - DoneCount;
    public int OverdueCount => Milestones.Count(m => m.Overdue);

    /// <summary>0-100 completion percentage (100 when there are no milestones).</summary>
    public int PercentComplete =>
        Total == 0 ? 100 : (int)Math.Round(100.0 * DoneCount / Total);

    public bool AllDone => Total > 0 && DoneCount == Total;

    /// <summary>The soonest not-yet-done milestone with a due date (the "next up").</summary>
    public SpeakerMilestone? NextUp =>
        Milestones
            .Where(m => !m.Done && m.DueDate is not null)
            .OrderBy(m => m.DueDate)
            .FirstOrDefault();
}

/// <summary>
/// Builds the <see cref="SpeakerMilestoneProgress"/> read-model for the
/// Speaker Hub. Reads the speaker's deadline tasks (SourceKey prefix
/// <c>speakerdl:</c>) and derives countdown + status. Deliberately decoupled
/// from how those tasks were seeded (absolute date vs offset), so it keeps
/// working whatever deadline model is in effect.
/// </summary>
public sealed class SpeakerMilestoneService
{
    /// <summary>SourceKey prefix the speaker-deadline seeder stamps on each milestone task.</summary>
    public const string SourceKeyPrefix = "speakerdl:";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SpeakerMilestoneService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Load the speaker's milestone progress. Returns an empty progress
    /// (Total = 0) when the participant has no deadline tasks yet.
    /// </summary>
    public async Task<SpeakerMilestoneProgress> GetProgressAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.AssignedParticipantId == participantId
                        && t.SourceKey != null
                        && t.SourceKey.StartsWith(SourceKeyPrefix))
            .Select(t => new
            {
                t.Id,
                t.Title,
                t.Description,
                t.DueDate,
                t.State,
                t.CompletedAt,
            })
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        var milestones = tasks
            .Select(t =>
            {
                var done = t.State == TaskState.Done;
                int? daysUntil = (t.DueDate is { } due && !done)
                    ? due.DayNumber - today.DayNumber
                    : (int?)null;
                return new SpeakerMilestone(
                    t.Id, t.Title, t.Description, t.DueDate, done, t.CompletedAt)
                {
                    DaysUntilDue = daysUntil,
                };
            })
            // Open first (so the speaker sees what's left), then by due date,
            // then completed at the bottom. Within each, soonest-due first.
            .OrderBy(m => m.Done)
            .ThenBy(m => m.DueDate ?? DateOnly.MaxValue)
            .ThenBy(m => m.Title, StringComparer.Ordinal)
            .ToList();

        return new SpeakerMilestoneProgress(milestones);
    }

    /// <summary>
    /// Toggle one of the speaker's own milestone tasks between Done and Open.
    /// Scoped hard to (eventId, participantId, speakerdl: prefix) so a speaker
    /// can only ever flip their own milestones. Returns true when a row was
    /// changed.
    /// </summary>
    public async Task<bool> ToggleAsync(
        int eventId, int participantId, int taskId, CancellationToken ct = default)
    {
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.Id == taskId
                 && t.EventId == eventId
                 && t.AssignedParticipantId == participantId
                 && t.SourceKey != null
                 && t.SourceKey.StartsWith(SourceKeyPrefix),
            ct);
        if (task is null) return false;

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
        return true;
    }
}
