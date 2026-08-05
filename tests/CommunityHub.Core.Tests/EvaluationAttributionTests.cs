using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §743 §5.3 — WHICH SESSION a press belongs to. This is the core mechanism of Session Evaluation:
/// every rule here is the difference between a press counting for the right speaker and it counting
/// for the one before them.
/// </summary>
public sealed class EvaluationAttributionTests
{
    private static readonly DateTimeOffset Day = new(2027, 2, 9, 0, 0, 0, TimeSpan.Zero);

    private static EvaluationSession S(string title, int startHour, int startMin, int endHour, int endMin)
    {
        var start = Day.AddHours(startHour).AddMinutes(startMin);
        var end = Day.AddHours(endHour).AddMinutes(endMin);
        var (opensAt, closesAt) = EvaluationAttribution.WindowFor(start, end);
        return new EvaluationSession
        {
            Id = title.GetHashCode() & 0x7fffffff,
            Title = title, ScheduledStart = start, ScheduledEnd = end,
            CollectionWindowOpensAt = opensAt, CollectionWindowClosesAt = closesAt,
        };
    }

    private static DateTimeOffset At(int h, int m) => Day.AddHours(h).AddMinutes(m);

    // The brief's own worked example: ABC 09:00–10:00, DEF 10:30–11:30, same room.
    private static readonly EvaluationSession Abc = S("ABC", 9, 0, 10, 0);
    private static readonly EvaluationSession Def = S("DEF", 10, 30, 11, 30);
    private static readonly EvaluationSession[] Room = { Abc, Def };

    [Fact]
    public void A_press_during_a_session_belongs_to_it()
    {
        Assert.Equal(Abc.Id, EvaluationAttribution.Resolve(Room, At(9, 12))?.Id);
        Assert.Equal(Def.Id, EvaluationAttribution.Resolve(Room, At(11, 0))?.Id);
    }

    [Fact]
    public void A_press_in_the_30_minute_tail_still_belongs_to_the_session_that_just_ended()
    {
        // The attendee pressing on their way out is the reason the grace period exists.
        Assert.Equal(Abc.Id, EvaluationAttribution.Resolve(Room, At(10, 20))?.Id);
    }

    [Fact]
    public void A_press_in_the_gap_beyond_the_tail_belongs_to_NOBODY_and_that_is_allowed()
    {
        // 10:30 is ABC's tail end and DEF's start; 10:29:59 is past nothing... but a press at
        // 10:29 IS inside ABC's tail. Take a moment genuinely outside both: ABC's tail ends at
        // 10:30 and DEF starts at 10:30, so construct a gap case instead.
        var lonely = new[] { S("ONLY", 9, 0, 10, 0) };

        // 10:31 is one minute past the 30-minute tail.
        Assert.Null(EvaluationAttribution.Resolve(lonely, At(10, 31)));
    }

    [Fact]
    public void THE_TIE_BREAK_the_session_in_progress_beats_the_previous_session_s_grace_tail()
    {
        // 🔥 The rule that stops presses aimed at the second talk being credited to the first.
        // Sessions back to back: 09:00–10:00 then 10:00–11:00. At 10:15 the FIRST session's tail
        // is still open (until 10:30) AND the second is in progress. The second must win.
        var first = S("FIRST", 9, 0, 10, 0);
        var second = S("SECOND", 10, 0, 11, 0);
        var room = new[] { first, second };

        var hit = EvaluationAttribution.Resolve(room, At(10, 15));

        Assert.Equal(second.Id, hit?.Id);
    }

    [Fact]
    public void On_a_boundary_the_press_belongs_to_the_session_STARTING_not_the_one_ending()
    {
        // Back-to-back with no gap: at exactly 10:00 both could claim the instant. The core
        // interval is start-inclusive and end-exclusive, so the new session takes it.
        var first = S("FIRST", 9, 0, 10, 0);
        var second = S("SECOND", 10, 0, 11, 0);

        Assert.Equal(second.Id, EvaluationAttribution.Resolve(new[] { first, second }, At(10, 0))?.Id);
    }

    [Fact]
    public void A_press_before_the_first_session_opens_belongs_to_nobody()
    {
        Assert.Null(EvaluationAttribution.Resolve(Room, At(8, 45)));
    }

    [Fact]
    public void An_empty_room_schedule_attributes_nothing_rather_than_throwing()
    {
        Assert.Null(EvaluationAttribution.Resolve(Array.Empty<EvaluationSession>(), At(9, 30)));
    }

    [Fact]
    public void The_grace_period_is_THIRTY_minutes_and_one_constant_drives_it()
    {
        // 🔒 §743 item 6: the figure is authoritative in both Part 1 and §5.3 and the two must
        // never diverge. Pinned so a change has to be deliberate and in one place.
        Assert.Equal(30, EvaluationAttribution.GraceMinutes);

        var (opens, closes) = EvaluationAttribution.WindowFor(At(9, 0), At(10, 0));
        Assert.Equal(At(9, 0), opens);
        Assert.Equal(At(10, 30), closes);
    }

    // ---- overlap validation ---------------------------------------------------------------

    [Fact]
    public void Genuinely_overlapping_sessions_are_detected()
    {
        Assert.True(EvaluationAttribution.Overlaps(At(9, 0), At(10, 0), At(9, 30), At(10, 30)));
    }

    [Fact]
    public void Back_to_back_sessions_are_NOT_an_overlap_even_though_the_grace_tails_cross()
    {
        // 🔑 Treating a crossing grace tail as a clash would reject every normal schedule. Only
        // the CORE intervals are compared — the tail overlap is what the tie-break exists for.
        Assert.False(EvaluationAttribution.Overlaps(At(9, 0), At(10, 0), At(10, 0), At(11, 0)));
        Assert.False(EvaluationAttribution.Overlaps(At(9, 0), At(10, 0), At(10, 15), At(11, 0)));
    }
}
