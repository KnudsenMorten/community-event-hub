using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1169 — the queue's default order: the NEXT post first, history underneath.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-03: <i>"i also dont want to show all published at the top, but the next
/// one"</i>. The queue sorted by scheduled date ascending, so a campaign that has been running since
/// August opened on August — and the thing he actually had to deal with was somewhere below the
/// fold.</para>
///
/// <para>🔑 <b>The same insight §936 already reached for the editor, applied to a mixed list.</b>
/// There: <i>"Planned and Scheduled are about what is COMING … Published is a HISTORY"</i>. A queue
/// holding both cannot use one direction for both halves — ascending buries the next post under
/// everything that already went out, and descending buries it under everything scheduled for
/// February.</para>
///
/// <para>⇒ Split at NOW: what is still to come, nearest first; then what has gone, most recent
/// first. The two meet in the middle at today, which is where he is reading from.</para>
///
/// <para>🔒 Nothing is hidden or dropped — every post is still in the list, in both halves. This is
/// ordering, not filtering: a queue that quietly stopped showing old posts would be a different and
/// much worse change.</para>
/// </remarks>
public static class SoMeQueueOrder
{
    /// <summary>Upcoming ascending, then past descending.</summary>
    /// <param name="now">
    /// The dividing line. ⚠️ Taken from the caller rather than read here, so the order is a pure
    /// function of its inputs and a test can pin the boundary instead of racing the clock.
    /// </param>
    public static IReadOnlyList<SoMePost> NextFirst(IEnumerable<SoMePost> posts, DateTimeOffset now)
    {
        var all = posts.ToList();

        // 🔑 The boundary is INCLUSIVE of now: a post due this very second is still the next one to
        // deal with, not history. Every post has a scheduled date (the column is not nullable), so
        // the two halves partition the list exactly — nothing can fall between them.
        var upcoming = all
            .Where(p => p.ScheduledAtUtc >= now)
            .OrderBy(p => p.ScheduledAtUtc)
            .ThenBy(p => p.Id)
            .ToList();

        var past = all
            .Where(p => p.ScheduledAtUtc < now)
            .OrderByDescending(p => p.ScheduledAtUtc)
            .ThenByDescending(p => p.Id)
            .ToList();

        return upcoming.Concat(past).ToList();
    }
}
