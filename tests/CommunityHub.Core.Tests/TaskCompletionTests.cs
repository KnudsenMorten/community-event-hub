using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §147: unit tests for the PURE <see cref="TaskCompletion"/> rollup ("X of N done" +
/// percent) shown at the top of every task page (volunteer/media/eventpartner /Tasks,
/// /Speaker/Tasks, /Sponsor/Tasks). No DB — synthetic <see cref="ParticipantTask"/>
/// lists so the maths is exact. FAKE data only.
/// </summary>
public sealed class TaskCompletionTests
{
    private static ParticipantTask T(TaskState state) =>
        new() { Title = "t", State = state };

    [Fact]
    public void Empty_list_is_zero_done_zero_total_zero_percent_not_all_done()
    {
        var c = TaskCompletion.From(System.Array.Empty<ParticipantTask>());

        Assert.Equal(0, c.Done);
        Assert.Equal(0, c.Total);
        Assert.Equal(0, c.Percent); // never a misleading 100% for an empty list
        Assert.False(c.AllDone);
        Assert.False(c.HasTasks);
    }

    [Fact]
    public void Counts_done_versus_total_across_all_states()
    {
        // Open + InProgress are both "not done"; only Done counts toward Done.
        var c = TaskCompletion.From(new[]
        {
            T(TaskState.Done),
            T(TaskState.Done),
            T(TaskState.Open),
            T(TaskState.InProgress),
        });

        Assert.Equal(2, c.Done);
        Assert.Equal(4, c.Total);
        Assert.Equal(50, c.Percent);
        Assert.False(c.AllDone);
        Assert.True(c.HasTasks);
    }

    [Fact]
    public void All_done_is_100_percent_and_all_done()
    {
        var c = TaskCompletion.From(new[] { T(TaskState.Done), T(TaskState.Done) });

        Assert.Equal(100, c.Percent);
        Assert.True(c.AllDone);
    }

    [Fact]
    public void Percent_rounds_to_nearest_away_from_midpoint()
    {
        // 1 of 3 = 33.33 -> 33
        Assert.Equal(33, TaskCompletion.From(new[]
        {
            T(TaskState.Done), T(TaskState.Open), T(TaskState.Open),
        }).Percent);

        // 2 of 3 = 66.66 -> 67
        Assert.Equal(67, TaskCompletion.From(new[]
        {
            T(TaskState.Done), T(TaskState.Done), T(TaskState.Open),
        }).Percent);

        // 1 of 8 = 12.5 -> 13 (MidpointRounding.AwayFromZero)
        Assert.Equal(13, TaskCompletion.From(new[]
        {
            T(TaskState.Done),
            T(TaskState.Open), T(TaskState.Open), T(TaskState.Open),
            T(TaskState.Open), T(TaskState.Open), T(TaskState.Open), T(TaskState.Open),
        }).Percent);
    }
}
