namespace CommunityHub.Core.Auth;

/// <summary>
/// Mints + validates the opaque, per-participant auto-login token carried in a
/// magic-link URL. The token authenticates the recipient on its own (a real
/// magic-link, not a forgeable <c>?email=</c> prefill): the receiving page
/// (<c>/Login/Magic</c>) validates it, establishes the session, and lands the
/// participant in their role hub.
///
/// This is a Core seam so the auto-login welcome email (which lives in Core,
/// next to <see cref="WelcomeEmailService"/>) can build a real sign-in link
/// without taking a dependency on the web project. The shipped implementation
/// is <see cref="MagicLinkTokenFactory"/> (ASP.NET DataProtection-signed,
/// tamper-evident, no secret in the URL).
/// </summary>
public interface IMagicLinkTokenFactory
{
    /// <summary>
    /// Produce an opaque, URL-safe auto-login token for one participant. A null
    /// <paramref name="ttl"/> uses the implementation's default lifetime.
    /// </summary>
    string CreateToken(int participantId, TimeSpan? ttl = null);

    /// <summary>
    /// Validate a token. Returns the ParticipantId, or null on any failure
    /// (tampered, expired, malformed). Never throws.
    /// </summary>
    int? ValidateToken(string token);
}
