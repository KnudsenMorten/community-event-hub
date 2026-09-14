using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1169 — the queue opens on the NEXT post, not on the start of the campaign.
///
/// <para>Operator 2026-09-03: <i>"i also dont want to show all published at the top, but the next
/// one"</i>. Sorted plainly by date, a campaign running since August opened on August, with the post
/// he actually had to act on below the fold.</para>
///
/// <para>🔑 The same split §936 already made for the editor — <i>"Planned and Scheduled are about
/// what is COMING … Published is a HISTORY"</i> — applied to a list that holds both. Ascending
/// buries the next post under everything already sent; descending buries it under everything
/// scheduled for February.</para>
/// </summary>
public sealed class SoMeQueueOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static SoMePost At(int id, DateTimeOffset when) =>
        new() { Id = id, EventId = 1, ScheduledAtUtc = when };

    [Fact]
    public void The_next_upcoming_post_is_first()
    {
        var posts = new[]
        {
            At(1, Now.AddDays(-30)),   // published long ago
            At(2, Now.AddDays(-1)),    // published yesterday
            At(3, Now.AddDays(1)),     // the next one
            At(4, Now.AddDays(30)),    // later
        };

        var ordered = SoMeQueueOrder.NextFirst(posts, Now);

        Assert.Equal(new[] { 3, 4, 2, 1 }, ordered.Select(p => p.Id));
    }

    /// <summary>
    /// 🔑 History reads NEWEST first, because you look at what just went out.
    /// </summary>
    [Fact]
    public void The_past_is_newest_first_underneath()
    {
        var posts = new[] { At(1, Now.AddDays(-30)), At(2, Now.AddDays(-2)), At(3, Now.AddDays(-10)) };

        Assert.Equal(new[] { 2, 3, 1 }, SoMeQueueOrder.NextFirst(posts, Now).Select(p => p.Id));
    }

    [Fact]
    public void Upcoming_reads_nearest_first()
    {
        var posts = new[] { At(1, Now.AddDays(30)), At(2, Now.AddDays(2)), At(3, Now.AddDays(10)) };

        Assert.Equal(new[] { 2, 3, 1 }, SoMeQueueOrder.NextFirst(posts, Now).Select(p => p.Id));
    }

    /// <summary>
    /// 🔒 NOTHING is dropped. This is ordering, not filtering.
    /// </summary>
    /// <remarks>
    /// A queue that quietly stopped showing old posts would be a different and much worse change
    /// than the one he asked for.
    /// </remarks>
    [Fact]
    public void Every_post_is_still_present()
    {
        var posts = Enumerable.Range(1, 20)
            .Select(i => At(i, Now.AddDays(i - 10)))
            .ToList();

        var ordered = SoMeQueueOrder.NextFirst(posts, Now);

        Assert.Equal(posts.Count, ordered.Count);
        Assert.Equal(posts.Select(p => p.Id).OrderBy(x => x), ordered.Select(p => p.Id).OrderBy(x => x));
    }

    /// <summary>
    /// ⚠️ The boundary is INCLUSIVE of now — a post due this second is the next one, not history.
    /// </summary>
    [Fact]
    public void A_post_due_exactly_now_counts_as_upcoming()
    {
        var posts = new[] { At(1, Now.AddDays(-1)), At(2, Now) };

        Assert.Equal(new[] { 2, 1 }, SoMeQueueOrder.NextFirst(posts, Now).Select(p => p.Id));
    }

    [Fact]
    public void Ties_are_broken_stably_so_the_page_does_not_shuffle_between_loads()
    {
        var same = Now.AddDays(5);
        var posts = new[] { At(9, same), At(3, same), At(7, same) };

        Assert.Equal(new[] { 3, 7, 9 }, SoMeQueueOrder.NextFirst(posts, Now).Select(p => p.Id));
    }

    [Fact]
    public void All_past_or_all_future_still_works()
    {
        var allPast = new[] { At(1, Now.AddDays(-5)), At(2, Now.AddDays(-1)) };
        Assert.Equal(new[] { 2, 1 }, SoMeQueueOrder.NextFirst(allPast, Now).Select(p => p.Id));

        var allFuture = new[] { At(1, Now.AddDays(5)), At(2, Now.AddDays(1)) };
        Assert.Equal(new[] { 2, 1 }, SoMeQueueOrder.NextFirst(allFuture, Now).Select(p => p.Id));

        Assert.Empty(SoMeQueueOrder.NextFirst(Array.Empty<SoMePost>(), Now));
    }
}
