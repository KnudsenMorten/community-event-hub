using System.Linq.Expressions;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Tasks;

/// <summary>
/// §1082 — RETIRE A TASK INSTEAD OF DELETING IT, AND KEEP RETIREMENTS OUT OF "WHAT I HAVE DONE".
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this exists.</b> On 2026-08-13 the sponsor orphan prune hard-deleted 15 production
/// rows — <b>four of them completed</b> — the first time a task definition was removed from the
/// registry. The instruction for that retirement had been *"auto-close the task (never delete)"*, and
/// the delete happened anyway because a second mechanism (the prune) reached the rows first.</para>
///
/// <para>🔑 <b>The operator's rule, which the codebase already agreed with in three places:</b>
/// <i>"do we agree that you dont delete, but close them (as they were inactive)"</i>. §502 deactivates
/// a leaver rather than deleting them; §253 tombstones; <c>ParticipantDeactivationService</c> already
/// closes tasks with <see cref="TaskClosedReason.AbandonedOnDeactivation"/> and re-opens them verbatim
/// if the person returns. Closing is reversible and auditable; deleting is neither.</para>
///
/// <para>⚠️ <b>The half that makes it safe.</b> A closed-by-the-system row is <c>State = Done</c>, so
/// without <see cref="IsSystemClosed"/> every "completed" list would silently absorb it and a sponsor
/// would be shown nine things they never did. The doc-comment on
/// <see cref="TaskClosedReason.AbandonedOnDeactivation"/> has always CLAIMED such rows are *"excluded
/// from completion ratios"* — measured 2026-08-13, <b>nothing implemented that</b>. This type is
/// where the claim becomes true.</para>
///
/// <para>🔒 <b>Hard deletion survives in exactly one place</b>, deliberately:
/// <c>WizardStepTaskSeeder</c>'s duplicate sweep, where two rows share one <c>SourceKey</c> and one is
/// redundant BY CONSTRUCTION. Closing it would leave a phantom "completed" twin beside the real row —
/// the only case where the row records nothing a human did or could see.</para>
/// </remarks>
public static class TaskClosure
{
    /// <summary>
    /// Retire a task: closed, labelled with WHY, and never deleted. Idempotent — a row already Done
    /// keeps its original <see cref="ParticipantTask.CompletedAt"/> and, crucially, its existing
    /// reason (or lack of one: a genuinely completed row must NOT acquire a system reason and vanish
    /// from the person's completed list).
    /// </summary>
    public static void Retire(ParticipantTask task, TaskClosedReason reason, DateTimeOffset now)
    {
        if (task.State == TaskState.Done) return;   // 🔒 real completions are left exactly as they are
        task.State = TaskState.Done;
        task.CompletedAt ??= now;
        task.ClosedReason = reason;
    }

    /// <summary>
    /// True when this row was closed BY THE SYSTEM rather than by a person doing the work.
    /// <para>🔑 <c>ClosedReason</c> is null on every task somebody actually completed — the column
    /// exists only to label the closures the hub made on someone's behalf (see
    /// <c>/Sponsor/Tasks</c>: *"ClosedReason stays NULL: that column labels closures the …"*). So the
    /// test is simply "is it set".</para>
    /// </summary>
    public static bool IsSystemClosed(ParticipantTask task) =>
        task.State == TaskState.Done && task.ClosedReason != null;

    /// <summary>
    /// The same rule as an EF expression, for the surfaces that filter in the database.
    /// ⚠️ Kept beside <see cref="IsSystemClosed"/> so the two cannot drift — §867.1's lesson.
    /// </summary>
    public static Expression<Func<ParticipantTask, bool>> NotSystemClosed =>
        t => t.State != TaskState.Done || t.ClosedReason == null;
}
