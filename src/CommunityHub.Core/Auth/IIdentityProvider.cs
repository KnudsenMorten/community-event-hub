using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Auth;

/// <summary>
/// The result of an identity-establishment attempt.
/// </summary>
public sealed record IdentityResult(
    bool Succeeded,
    Participant? Profile,
    string? FailureReason)
{
    public static IdentityResult Success(Participant profile) =>
        new(true, profile, null);

    public static IdentityResult Fail(string reason) =>
        new(false, null, reason);
}

/// <summary>
/// The single seam through which a participant's identity is established.
/// CONTEXT.md section 5a: the hub builds Option B (one-tap PIN) now, but the
/// auth code must isolate identity establishment behind THIS abstraction so
/// that Option A (a verified Backstage SSO token) can be added later as a
/// second implementation - with no rewrite.
///
/// Stage 3 ships <c>PinIdentityProvider</c>. If Zoho Backstage is later
/// confirmed to emit a cryptographically signed token, a
/// <c>BackstageTokenIdentityProvider</c> is added alongside it - the rest of
/// the app depends only on this interface.
///
/// IMPORTANT: an unsigned / forgeable identity claim (e.g. a bare email in an
/// iframe URL) must NEVER satisfy this contract. Only a verified PIN, or a
/// cryptographically verified token, may return <see cref="IdentityResult.Success"/>.
/// </summary>
public interface IIdentityProvider
{
    /// <summary>
    /// A short stable name for the provider, e.g. "pin" or "backstage-sso".
    /// Used for diagnostics and to let configuration select a provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Attempt to establish identity from the supplied claim within the given
    /// event edition. The claim is provider-specific (a redeemed PIN, a signed
    /// token, ...). Implementations must verify the claim before succeeding.
    /// </summary>
    Task<IdentityResult> EstablishIdentityAsync(
        int eventId,
        IdentityClaim claim,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A provider-agnostic carrier for whatever the caller is presenting as proof
/// of identity. Each provider reads only the fields it understands.
/// </summary>
public sealed record IdentityClaim
{
    /// <summary>The email the person is identifying as. Always lower-cased/trimmed by the caller.</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>For the PIN provider: the 6-digit PIN the user entered.</summary>
    public string? Pin { get; init; }

    /// <summary>For a future SSO provider: the signed token to verify. Never trusted unverified.</summary>
    public string? SignedToken { get; init; }
}
