using CommunityHub.Core.Auth;
using Microsoft.AspNetCore.DataProtection;

namespace CommunityHub.Auth;

/// <summary>
/// One-tap sign-in via a signed URL (the magic-link). Kept as the web-project
/// type the pages already inject; the token logic now lives in Core's
/// <see cref="MagicLinkTokenFactory"/> so the auto-login welcome email can mint
/// the same token without depending on the web project. The token contract
/// (purpose string + payload shape) is unchanged, so links minted before and
/// after this refactor are byte-compatible and validate identically.
///
/// Tokens are NOT single-use: anyone with the URL can sign in for the
/// participant within the expiry window (bulk-mail distribution where reshare
/// exposure is acceptable; a leaked link is mitigated by the participant using
/// email + PIN and the organizer re-sending).
/// </summary>
public sealed class MagicLinkService : IMagicLinkTokenFactory
{
    public static readonly TimeSpan DefaultTtl = MagicLinkTokenFactory.DefaultTtl;

    private readonly MagicLinkTokenFactory _factory;

    public MagicLinkService(IDataProtectionProvider dataProtectionProvider)
    {
        _factory = new MagicLinkTokenFactory(dataProtectionProvider);
    }

    /// <summary>Produce an opaque, URL-safe token for one participant.</summary>
    public string CreateToken(int participantId, TimeSpan? ttl = null) =>
        _factory.CreateToken(participantId, ttl);

    /// <summary>Verify a token. Returns the ParticipantId or null on any failure.</summary>
    public int? ValidateToken(string token) =>
        _factory.ValidateToken(token);
}
