using CommunityHub.Core.Auth;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the magic-link token factory (REQUIREMENTS §21 "Login recovery
/// states"). The recovery path on /Login/Magic needs to tell an EXPIRED-but-
/// genuine link apart from a tampered/alien one so it can pre-fill the
/// recipient's email and offer a one-tap "request a new code". That is exactly
/// what <see cref="MagicLinkTokenFactory.PeekParticipantId"/> does — WITHOUT ever
/// authenticating (sign-in still needs a live token via ValidateToken).
///
/// All offline: the ephemeral DataProtection provider, no app/DB/secrets.
/// </summary>
public sealed class MagicLinkTokenFactoryTests
{
    private static MagicLinkTokenFactory NewFactory() =>
        new(DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-dp-tests"))));

    [Fact]
    public void ValidateToken_round_trips_a_live_token()
    {
        var f = NewFactory();
        var token = f.CreateToken(participantId: 42, ttl: TimeSpan.FromMinutes(10));

        Assert.Equal(42, f.ValidateToken(token));
    }

    [Fact]
    public void ValidateToken_rejects_an_expired_token()
    {
        var f = NewFactory();
        // A negative TTL produces an already-expired token.
        var token = f.CreateToken(participantId: 42, ttl: TimeSpan.FromMinutes(-1));

        Assert.Null(f.ValidateToken(token));
    }

    [Fact]
    public void PeekParticipantId_recovers_the_id_from_an_expired_but_genuine_token()
    {
        var f = NewFactory();
        var token = f.CreateToken(participantId: 42, ttl: TimeSpan.FromMinutes(-1));

        // ValidateToken refuses it (so we never sign in)...
        Assert.Null(f.ValidateToken(token));
        // ...but Peek still recovers who it was for, to drive the recovery state.
        Assert.Equal(42, f.PeekParticipantId(token));
    }

    [Fact]
    public void PeekParticipantId_recovers_the_id_from_a_live_token_too()
    {
        var f = NewFactory();
        var token = f.CreateToken(participantId: 7, ttl: TimeSpan.FromMinutes(10));

        Assert.Equal(7, f.PeekParticipantId(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    [InlineData("AAAA-BBBB_CCCC")]
    public void PeekParticipantId_returns_null_for_a_tampered_or_alien_token(string token)
    {
        var f = NewFactory();
        Assert.Null(f.PeekParticipantId(token));
    }

    [Fact]
    public void PeekParticipantId_returns_null_when_the_token_is_tampered()
    {
        var f = NewFactory();
        var token = f.CreateToken(participantId: 42, ttl: TimeSpan.FromMinutes(10));

        // Flip a character in the middle so the DataProtection MAC fails.
        var mid = token.Length / 2;
        var tampered = token[..mid] + (token[mid] == 'A' ? 'B' : 'A') + token[(mid + 1)..];

        Assert.Null(f.PeekParticipantId(tampered));
    }

    [Fact]
    public void A_token_minted_by_a_different_key_does_not_peek()
    {
        // Two factories with different key rings: a token from one must be opaque
        // to the other (proves Peek is gated by the MAC, not just JSON-shaped).
        var a = new MagicLinkTokenFactory(DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-dp-tests-a"))));
        var b = new MagicLinkTokenFactory(DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-dp-tests-b"))));

        var token = a.CreateToken(participantId: 99, ttl: TimeSpan.FromMinutes(10));

        Assert.Null(b.PeekParticipantId(token));
    }
}
