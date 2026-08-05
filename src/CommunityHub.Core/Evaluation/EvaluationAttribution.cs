using CommunityHub.Core.Domain.Evaluation;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §743 §5.3 — WHICH SESSION a press belongs to. The core mechanism of the whole system: if this is
/// wrong, a press is counted against the wrong speaker.
/// </summary>
/// <remarks>
/// <para>A response is attributable to a session when its <b>collection timestamp</b> falls inside
/// that session's collection window — the scheduled start through <see cref="GraceMinutes"/> after
/// the scheduled end. Never the received timestamp: a record collected inside the window belongs to
/// that session no matter how late it is uploaded.</para>
///
/// <para>🔒 <b>THE TIE-BREAK.</b> Two sessions may never overlap in one room — that is rejected as a
/// validation error, because attribution would be genuinely ambiguous and no rule could recover the
/// attendee's intent. What legitimately overlaps is one session's 30-minute grace tail and the
/// NEXT session's scheduled start. When a timestamp falls in both, <b>the session actually in
/// progress wins over the grace tail of the preceding one.</b> Without this rule, presses aimed at
/// the second talk are credited to the first.</para>
/// </remarks>
public static class EvaluationAttribution
{
    /// <summary>
    /// The grace period after a session's scheduled end, in minutes.
    /// </summary>
    /// <remarks>
    /// 🔒 §743 item 6: this figure is authoritative in BOTH Part 1 and §5.3, and <b>ONE value drives
    /// both — they must never be allowed to diverge</b>. It is referenced here and by the window
    /// stamped onto <see cref="EvaluationSession.CollectionWindowClosesAt"/>; nothing else may
    /// hard-code 30.
    /// </remarks>
    public const int GraceMinutes = 30;

    /// <summary>The collection window for a scheduled slot: start → end + grace.</summary>
    public static (DateTimeOffset OpensAt, DateTimeOffset ClosesAt) WindowFor(
        DateTimeOffset scheduledStart, DateTimeOffset scheduledEnd) =>
        (scheduledStart, scheduledEnd.AddMinutes(GraceMinutes));

    /// <summary>
    /// Pick the session a press at <paramref name="collectedAt"/> belongs to, among the candidate
    /// sessions of ONE room. Returns null when no window contains it — a legitimate, documented
    /// state (§5.3): the response is stored and attributed to the venue and date only.
    /// </summary>
    /// <remarks>
    /// Two passes, in this order, and the order IS the rule:
    /// <list type="number">
    ///   <item>a session whose CORE interval (scheduled start → scheduled end) contains the
    ///     timestamp — the session actually in progress;</item>
    ///   <item>only if none does, a session whose GRACE TAIL contains it.</item>
    /// </list>
    /// Ties inside one pass are broken by the later start, so the most recent match wins rather
    /// than an arbitrary one.
    /// </remarks>
    public static EvaluationSession? Resolve(
        IEnumerable<EvaluationSession> roomSessions, DateTimeOffset collectedAt)
    {
        EvaluationSession? inProgress = null;
        EvaluationSession? inGrace = null;

        foreach (var s in roomSessions)
        {
            // Core interval — inclusive of the start, exclusive of the end, so a press exactly at
            // a boundary belongs to the session STARTING rather than the one ending. Back-to-back
            // sessions in one room otherwise both claim that instant.
            if (collectedAt >= s.ScheduledStart && collectedAt < s.ScheduledEnd)
            {
                if (inProgress is null || s.ScheduledStart > inProgress.ScheduledStart)
                    inProgress = s;
                continue;
            }

            if (collectedAt >= s.CollectionWindowOpensAt && collectedAt < s.CollectionWindowClosesAt)
            {
                if (inGrace is null || s.ScheduledStart > inGrace.ScheduledStart)
                    inGrace = s;
            }
        }

        // 🔒 In progress beats a grace tail. Always.
        return inProgress ?? inGrace;
    }

    /// <summary>
    /// Do two scheduled slots overlap in the same room? Used to REJECT such a schedule outright
    /// (§743 item 32) rather than invent a tie-break for an ambiguity no rule can resolve.
    /// </summary>
    /// <remarks>
    /// Compares CORE intervals only. Grace tails are expected to overlap the next session and are
    /// handled by <see cref="Resolve"/> — treating that as a clash would reject every normal
    /// back-to-back schedule.
    /// </remarks>
    public static bool Overlaps(
        DateTimeOffset startA, DateTimeOffset endA, DateTimeOffset startB, DateTimeOffset endB) =>
        startA < endB && startB < endA;
}
