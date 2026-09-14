using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1082 — RETIRE, DO NOT DELETE — AND NEVER LET A RETIREMENT READ AS A COMPLETION.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-13, after the sponsor orphan prune hard-deleted 15 production rows (four of
/// them completed): <i>"when you implement a guard, do we agree that you dont delete, but close them
/// (as they were inactive)"</i>. He is right, and the hub already said so in three places — §502
/// deactivates a leaver, §253 tombstones, and <c>ParticipantDeactivationService</c> closes tasks with
/// a reason and re-opens them verbatim on reactivation.</para>
///
/// <para>🔑 <b>The dangerous half is the second one.</b> A retired row is <c>State = Done</c>, so
/// every "completed" list absorbs it unless something filters on <c>ClosedReason</c> — and measured
/// on 2026-08-13, <b>nothing did</b>, despite the doc-comment on <c>AbandonedOnDeactivation</c>
/// claiming such rows are *"excluded from completion ratios"*. Closing without filtering would have
/// told a sponsor they completed nine tasks they never saw.</para>
/// </remarks>
public sealed class TaskClosureTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static ParticipantTask Open() => new()
    {
        EventId = 1, Title = "Upload your booth artwork", State = TaskState.Open,
        SourceKey = "sponsor:4242:upload-sponsor-wall-design-in-vector-format",
    };

    [Fact]
    public void Retire_closes_the_task_and_labels_why()
    {
        var t = Open();
        TaskClosure.Retire(t, TaskClosedReason.RetiredFromCatalog, Now);

        Assert.Equal(TaskState.Done, t.State);
        Assert.Equal(Now, t.CompletedAt);
        Assert.Equal(TaskClosedReason.RetiredFromCatalog, t.ClosedReason);
        Assert.True(TaskClosure.IsSystemClosed(t));
    }

    /// <summary>
    /// 🔴 THE ONE THAT PROTECTS REAL WORK. A task somebody actually completed must keep its own
    /// <c>CompletedAt</c> and must NOT acquire a system reason — otherwise retiring a catalog entry
    /// would erase the fact that a person finished it, which is the very thing the delete did.
    /// </summary>
    [Fact]
    public void Retire_never_overwrites_a_real_completion()
    {
        var completedAt = Now.AddDays(-10);
        var t = Open();
        t.State = TaskState.Done;
        t.CompletedAt = completedAt;
        t.CompletedByParticipantId = 77;
        t.ClosedReason = null;               // a person did this

        TaskClosure.Retire(t, TaskClosedReason.RetiredFromCatalog, Now);

        Assert.Equal(completedAt, t.CompletedAt);
        Assert.Equal(77, t.CompletedByParticipantId);
        Assert.Null(t.ClosedReason);
        Assert.False(TaskClosure.IsSystemClosed(t));   // still reads as THEIR completion
    }

    /// <summary>A genuinely completed task is never "system closed" — that is what keeps it visible
    /// as the person's own work.</summary>
    [Fact]
    public void A_real_completion_is_not_system_closed()
    {
        var t = Open();
        t.State = TaskState.Done;
        t.CompletedAt = Now;
        Assert.False(TaskClosure.IsSystemClosed(t));
    }

    /// <summary>An open task is not system-closed either, obviously — but the check is cheap and it
    /// is the guard that stops a filter accidentally hiding live work.</summary>
    [Fact]
    public void An_open_task_is_not_system_closed()
    {
        Assert.False(TaskClosure.IsSystemClosed(Open()));
    }

    /// <summary>
    /// 🔒 Both closure reasons count as system-closed. <c>AbandonedOnDeactivation</c> predates this
    /// type and its doc-comment always claimed such rows were excluded from completion ratios;
    /// including it here is what finally makes that true.
    /// </summary>
    [Theory]
    [InlineData(TaskClosedReason.AbandonedOnDeactivation)]
    [InlineData(TaskClosedReason.SupersededByGetStarted)]
    [InlineData(TaskClosedReason.RetiredFromCatalog)]
    public void Every_system_reason_marks_the_row_as_not_the_persons_work(TaskClosedReason reason)
    {
        var t = Open();
        TaskClosure.Retire(t, reason, Now);
        Assert.True(TaskClosure.IsSystemClosed(t));
    }

    /// <summary>
    /// 🔴 THE BUG "CLOSE INSTEAD OF DELETE" INTRODUCED, AND THE GUARD THAT CLOSES IT.
    /// </summary>
    /// <remarks>
    /// <para><c>FormTaskReconciler</c> syncs certain tasks BOTH ways off a data signal: answered ⇒
    /// Done, un-answered ⇒ <b>reopen</b>. That is right for work a person completed. It is wrong for
    /// a row the SYSTEM closed — a retired task has no data by definition, so the reconciler
    /// resurrected it on the very next page load, and the retirement silently undid itself.</para>
    ///
    /// <para>⚠️ This only appeared once the prunes started retiring instead of deleting: a deleted
    /// row cannot be reopened. It is the same shape as the §1081 reset key — a decision that reverses
    /// itself is worse than no decision — and it is why <c>ClosedReason</c> has to gate the reopen.</para>
    /// </remarks>
    [Fact]
    public void A_system_closed_task_must_not_look_reopenable()
    {
        var retired = Open();
        TaskClosure.Retire(retired, TaskClosedReason.RetiredFromCatalog, Now);

        // The reconciler's reopen condition is "Done AND nobody's own completion".
        static bool WouldReopen(ParticipantTask t) =>
            t.State == TaskState.Done && t.ClosedReason == null;

        Assert.False(WouldReopen(retired));

        // Control: a task a PERSON completed is still reopened when their answer disappears —
        // the behaviour the guard must not break.
        var theirs = Open();
        theirs.State = TaskState.Done;
        theirs.CompletedAt = Now;
        Assert.True(WouldReopen(theirs));
    }

    /// <summary>
    /// 🔒 §867.1's lesson: the in-memory predicate and the EF expression are two statements of one
    /// rule, so they are asserted to agree rather than trusted to.
    /// </summary>
    [Fact]
    public void The_expression_and_the_predicate_agree()
    {
        var rows = new List<ParticipantTask>();

        var open = Open(); rows.Add(open);
        var realDone = Open(); realDone.State = TaskState.Done; realDone.CompletedAt = Now; rows.Add(realDone);
        var retired = Open(); TaskClosure.Retire(retired, TaskClosedReason.RetiredFromCatalog, Now); rows.Add(retired);
        var abandoned = Open(); TaskClosure.Retire(abandoned, TaskClosedReason.AbandonedOnDeactivation, Now); rows.Add(abandoned);

        var byExpression = rows.AsQueryable().Where(TaskClosure.NotSystemClosed).ToList();
        var byPredicate = rows.Where(t => !TaskClosure.IsSystemClosed(t)).ToList();

        Assert.Equal(new[] { open, realDone }, byExpression);
        Assert.Equal(byExpression, byPredicate);
    }
}
