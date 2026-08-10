using System;
using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1040 — WHEN THE SHARED LINK STOPS WORKING.
/// </summary>
/// <remarks>
/// <para>The token IS the credential for a page that shows real people's names and e-mail addresses
/// with no login, so the two ways to close it — revoke now, expire by itself — are the security of
/// this feature and not bookkeeping.</para>
///
/// <para>Operator 2026-08-10: <i>"link must be active until 15 feb 2027 (after event)"</i>.</para>
/// </remarks>
public sealed class AttendeeMonitorAccessTests
{
    private static AttendeeMonitor New(
        DateTimeOffset? expires = null, DateTimeOffset? revoked = null) =>
        new()
        {
            EventId = 1, Name = "Arrow volume package",
            Kind = AttendeeMonitorKind.EmailDomain, Value = "arrow.com",
            Token = AttendeeMonitor.NewToken(),
            ExpiresAt = expires, RevokedAt = revoked,
        };

    /// <summary>🔑 His date, as the default — nobody has to remember to set it.</summary>
    [Fact]
    public void A_new_monitor_expires_after_the_event_by_default()
    {
        Assert.Equal(new DateTimeOffset(2027, 2, 15, 23, 59, 59, TimeSpan.Zero),
            new AttendeeMonitor().ExpiresAt);
    }

    [Fact]
    public void It_is_open_before_the_expiry_and_closed_after()
    {
        var m = New(expires: new DateTimeOffset(2027, 2, 15, 23, 59, 59, TimeSpan.Zero));

        Assert.False(m.IsExpired(new DateTimeOffset(2027, 2, 15, 23, 59, 58, TimeSpan.Zero)));
        Assert.True(m.IsExpired(new DateTimeOffset(2027, 2, 15, 23, 59, 59, TimeSpan.Zero)));
        Assert.True(m.IsExpired(new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>🔒 Revocation closes it immediately, whatever the expiry says.</summary>
    [Fact]
    public void Revoking_closes_it_even_with_a_future_expiry()
    {
        var m = New(
            expires: new DateTimeOffset(2027, 2, 15, 0, 0, 0, TimeSpan.Zero),
            revoked: DateTimeOffset.UtcNow);

        Assert.False(m.IsActive);
    }

    /// <summary>
    /// ⚠️ A monitor with NO expiry is still valid — an organizer may clear the date deliberately.
    /// It is then closable only by revoking, which is why the field defaults to a date rather than
    /// to null.
    /// </summary>
    [Fact]
    public void No_expiry_means_it_never_expires_on_its_own()
    {
        var m = New(expires: null);

        Assert.False(m.IsExpired(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(m.IsActive);
    }
}
