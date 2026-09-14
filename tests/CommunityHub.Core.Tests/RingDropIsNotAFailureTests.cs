using CommunityHub.Core.Data;
using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1061 — A MAIL A ROLLOUT RING HELD BACK IS NOT A FAILURE, ANYWHERE.
/// </summary>
/// <remarks>
/// <para>One defect wearing three faces, all found in PROD on 2026-08-11 within an hour:</para>
/// <list type="number">
/// <item>the Audit page showed ~18 held mails as <b>Failure</b> — <i>"this is just noise now and
/// makes it hard to view and follow"</i>;</item>
/// <item>the Background-jobs page declared <b>"Outbound e-mail is DOWN — the last 16 sends all
/// failed"</b> while quoting its own reason: <i>"Ring-dropped (recipient outside the released ring)
/// — not sent"</i>;</item>
/// <item>and the two stores disagreed: <c>EmailLog.Dropped</c> said <i>dropped</i> while the audit
/// row written five lines later said <i>failed</i>, about the same send.</item>
/// </list>
///
/// <para>🔴 <b>(2) is the dangerous one.</b> The alarm that exists to say mail is broken fired
/// because mail was working exactly as configured — during a ticket sale, on a page he was watching.
/// An alarm that cries wolf is worse than no alarm: the next one is the one he ignores.</para>
/// </remarks>
public sealed class RingDropIsNotAFailureTests
{
    private const string RingDrop = "Ring-dropped (recipient outside the released ring) — not sent.";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"ringdrop-{Guid.NewGuid():N}").Options);

    private static EmailLog Log(bool dropped, string? error, int minutesAgo) => new()
    {
        EventId = 1,
        Category = "some-announcement",
        ToEmail = $"s{minutesAgo}@test.dk",
        Subject = "your announcement is scheduled",
        Success = false,
        Dropped = dropped,
        Error = error,
        SentAt = Now.AddMinutes(-minutesAgo),
    };

    /// <summary>
    /// 🔴 THE FALSE ALARM, reproduced: 16 ring-held mails and nothing else. Before the fix this
    /// reported "Outbound e-mail is DOWN".
    /// </summary>
    [Fact]
    public async Task Sixteen_ring_held_mails_do_not_mean_outbound_email_is_down()
    {
        using var db = NewDb();
        for (var i = 1; i <= 16; i++) db.EmailLogs.Add(Log(dropped: true, RingDrop, i));
        await db.SaveChangesAsync();

        var status = await new EmailTransportHealth(db, new FixedClock(Now)).CheckAsync();

        Assert.False(status.IsDown);
    }

    /// <summary>
    /// 🔒 …and the alarm must still WORK. A test that only proved silence would pass just as well on
    /// a detector that had been switched off — which is the more dangerous bug of the two.
    /// </summary>
    [Fact]
    public async Task Real_send_failures_still_raise_the_alarm()
    {
        using var db = NewDb();
        for (var i = 1; i <= 16; i++)
            db.EmailLogs.Add(Log(dropped: false, "SMTP 535 authentication failed", i));
        await db.SaveChangesAsync();

        var status = await new EmailTransportHealth(db, new FixedClock(Now)).CheckAsync();

        Assert.True(status.IsDown);
    }

    /// <summary>
    /// ⚠️ The subtle one: holds are IGNORED, not treated as successes. A dead relay with a few ring
    /// holds interleaved must still read as down — if a hold ended the streak, one held mail would
    /// make a genuine outage look recovered.
    /// </summary>
    [Fact]
    public async Task A_hold_between_real_failures_does_not_hide_an_outage()
    {
        using var db = NewDb();
        for (var i = 1; i <= 20; i++)
        {
            db.EmailLogs.Add(i % 4 == 0
                ? Log(dropped: true, RingDrop, i)
                : Log(dropped: false, "SMTP 421 service not available", i));
        }
        await db.SaveChangesAsync();

        var status = await new EmailTransportHealth(db, new FixedClock(Now)).CheckAsync();

        Assert.True(status.IsDown);
    }

    /// <summary>🔒 A real delivery still ends the streak — the pre-existing rule, unchanged.</summary>
    [Fact]
    public async Task A_successful_send_still_clears_the_alarm()
    {
        using var db = NewDb();
        for (var i = 2; i <= 20; i++) db.EmailLogs.Add(Log(dropped: false, "SMTP 421", i));
        var ok = Log(dropped: false, null, 1);
        ok.Success = true;
        db.EmailLogs.Add(ok);            // newest
        await db.SaveChangesAsync();

        var status = await new EmailTransportHealth(db, new FixedClock(Now)).CheckAsync();

        Assert.False(status.IsDown);
    }

    /// <summary>
    /// §1061 — the audit row must carry <see cref="AuditOutcome.Dropped"/>, not
    /// <see cref="AuditOutcome.Failure"/>. ⚠️ And NOT <see cref="AuditOutcome.Denied"/>, which
    /// already means an ACTOR was refused permission — reusing it would make "who was refused
    /// access?" unanswerable.
    /// </summary>
    [Fact]
    public void The_dropped_outcome_is_its_own_value_and_not_a_reused_one()
    {
        Assert.NotEqual(AuditOutcome.Failure, AuditOutcome.Dropped);
        Assert.NotEqual(AuditOutcome.Denied, AuditOutcome.Dropped);
        // Additive: the existing three keep their stored ints, so old rows keep their meaning.
        Assert.Equal(0, (int)AuditOutcome.Success);
        Assert.Equal(1, (int)AuditOutcome.Failure);
        Assert.Equal(2, (int)AuditOutcome.Denied);
        Assert.Equal(3, (int)AuditOutcome.Dropped);
    }
}
