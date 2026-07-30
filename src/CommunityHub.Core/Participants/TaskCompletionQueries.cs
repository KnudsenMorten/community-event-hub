using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Participants;

/// <summary>
/// The ONE definition of "a task that counts towards completion" (§332, closing the §326bj
/// dashboard bug).
///
/// A deactivated person's untouched work is closed as <see cref="TaskState.Done"/> — the state
/// has to be Done or the §81 reminder track would keep mailing deadline chasers to somebody who
/// left. So the raw table cannot tell "they did it" from "they walked away", and every
/// completion ratio in the product counted abandoned work as finished (the operator's
/// "Task completion 40 / 390" tile included the abandoned tasks of everyone who dropped out,
/// in BOTH numerator and denominator).
///
/// Ratios call <see cref="ExcludingAbandoned{T}"/> so abandoned rows leave both sides of the
/// fraction: the percentage then describes work that is still real. LISTS and the reminder
/// track deliberately do NOT call it — an abandoned task is legitimately closed and must stay
/// out of anyone's to-do.
/// </summary>
public static class TaskCompletionQueries
{
    /// <summary>
    /// Drop tasks the system closed on a departed assignee's behalf.
    /// </summary>
    /// <remarks>
    /// Tests <c>ClosedReason == null</c> rather than <c>!= AbandonedOnDeactivation</c> on
    /// purpose: in SQL, <c>ClosedReason &lt;&gt; 1</c> evaluates to UNKNOWN for a NULL and would
    /// silently discard EVERY normally-completed task — the ratio would then read 100%. If a
    /// second <see cref="TaskClosedReason"/> is ever added that SHOULD count, change this to an
    /// explicit <c>== null || ClosedReason == thatOne</c>, never to a <c>!=</c>.
    /// </remarks>
    public static IQueryable<ParticipantTask> ExcludingAbandoned(this IQueryable<ParticipantTask> q)
        => q.Where(t => t.ClosedReason == null);

    /// <summary>In-memory counterpart for callers that already materialised the rows.</summary>
    public static IEnumerable<T> ExcludingAbandoned<T>(
        this IEnumerable<T> rows, Func<T, TaskClosedReason?> reason)
        => rows.Where(r => reason(r) is null);
}
