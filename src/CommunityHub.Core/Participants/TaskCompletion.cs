using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Participants;

/// <summary>
/// §147: a PURE, role-agnostic rollup of <see cref="ParticipantTask"/> completion —
/// "X of N done" + a 0–100 percent. This is the SAME completion measure shown at the
/// top of every task page (volunteer/media/eventpartner <c>/Tasks</c>, <c>/Speaker/Tasks</c>,
/// <c>/Sponsor/Tasks</c>) so all roles read identically.
///
/// It is deliberately DISTINCT from the speaker readiness (§134/§144) and sponsor
/// deliverables (§135/§145) rollups: those aggregate richer per-stage signals
/// (profile fields, uploads, booth materials), whereas this is purely "how many of my
/// assigned tasks are marked Done". The UI labels them differently so the two reads
/// never compete.
/// </summary>
public readonly record struct TaskCompletion(int Done, int Total)
{
    /// <summary>Completion as a 0–100 percentage (rounded). 0 when there are no tasks,
    /// so an empty list never reads as a misleading 100%.</summary>
    public int Percent =>
        Total <= 0 ? 0 : (int)Math.Round(100.0 * Done / Total, MidpointRounding.AwayFromZero);

    /// <summary>True only when there is at least one task AND every task is done.</summary>
    public bool AllDone => Total > 0 && Done >= Total;

    /// <summary>True when there is anything to show a progress bar for.</summary>
    public bool HasTasks => Total > 0;

    /// <summary>Count Done / Total across a task list (Done = <see cref="TaskState.Done"/>).</summary>
    public static TaskCompletion From(IEnumerable<ParticipantTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        int done = 0, total = 0;
        foreach (var t in tasks)
        {
            total++;
            if (t.State == TaskState.Done) done++;
        }
        return new TaskCompletion(done, total);
    }
}
