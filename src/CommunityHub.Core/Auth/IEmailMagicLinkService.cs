namespace CommunityHub.Core.Auth;

/// <summary>
/// The §169 <b>personal email magic-link</b>: the ONE long-lived (365-day),
/// REUSABLE, per-participant + per-edition auto-login link that every in-hub
/// email CTA routes through, so a recipient lands signed-in with no email + PIN
/// step. It is deliberately the inverse trade-off of the welcome auto-login
/// (<see cref="IWelcomeAutoLoginTokenService"/>, single-use + short-lived):
///
/// <list type="bullet">
///   <item><b>Reusable</b> — the same link works from every email that person
///     gets, for a year; redemption never consumes it.</item>
///   <item><b>One per participant</b> — <see cref="GetOrCreateTokenAsync"/> reuses
///     the live grant rather than minting a new token per email.</item>
///   <item><b>Sign-in scope only</b> — it just establishes the normal session
///     (same claims/cookie path as a PIN sign-in); it bypasses NO per-action
///     check.</item>
///   <item><b>Hashed at rest</b> — only the SHA-256 of the random token is used
///     to look the grant up; the cleartext token lives only in the emailed URL.</item>
///   <item><b>Revocable + rotatable + logged</b> — backed by a
///     <c>MagicLinkGrant</c> the organizer can revoke / rotate; each redemption
///     stamps last-used + use-count and writes an audit entry.</item>
///   <item><b>Bound to participant + edition</b> — a token for one person in one
///     edition never signs in anyone else.</item>
/// </list>
///
/// <para><b>EXPOSURE NOTE (operator-accepted, REQUIREMENTS §169).</b> A 1-year
/// reusable link in email means anyone who obtains the email (forward, shared
/// inbox, mail backups) can sign in AS that person until it expires. This is an
/// accepted trade-off for a low-sensitivity event hub in exchange for the
/// convenience win; the mitigations above (sign-in-only scope, revoke, rotate,
/// usage logging, participant+edition binding) bound the blast radius.</para>
///
/// Lives in Core so every Core email builder can compose links without a web
/// dependency; the web <c>/go/{token}</c> page redeems it and signs the user in
/// via the shared sign-in path.
/// </summary>
public interface IEmailMagicLinkService
{
    /// <summary>
    /// Return the participant's standing reusable token, reusing the live grant
    /// when one exists (so every email carries the SAME link) and minting one on
    /// first use. Throws only for an unknown participant.
    /// </summary>
    Task<string> GetOrCreateTokenAsync(int participantId, CancellationToken ct = default);

    /// <summary>
    /// Compose the absolute auto-login URL for a token:
    /// <c>{baseUrl}/go/{token}</c>, optionally carrying a safe local deep-link
    /// target as <c>?r=/Path</c> (only "/"-prefixed, non-protocol-relative paths
    /// are honoured — anything else is dropped).
    /// </summary>
    string BuildUrl(string baseUrl, string token, string? localPath = null);

    /// <summary>
    /// Convenience: <see cref="GetOrCreateTokenAsync"/> + <see cref="BuildUrl"/>
    /// in one call — the single seam the email/link builder uses so every hub CTA
    /// carries the participant's token (+ deep-link target).
    /// </summary>
    Task<string> BuildUrlForParticipantAsync(
        int participantId, string baseUrl, string? localPath = null, CancellationToken ct = default);

    /// <summary>
    /// Redeem a presented token: validate (genuine, unexpired, not revoked, active
    /// participant, edition matches), stamp last-used + use-count, audit, and
    /// return the participant to sign in. NEVER signs in (the web page does that
    /// via the shared path) and NEVER throws — any failure returns
    /// <see cref="EmailMagicLinkResolution.Fail"/>, carrying the recovery email
    /// when the token is genuine-but-dead so the login page can pre-stage it.
    /// </summary>
    Task<EmailMagicLinkResolution> ResolveAsync(string token, CancellationToken ct = default);
}

/// <summary>
/// The outcome of redeeming a personal email magic-link. On success carries the
/// participant id to establish the session for; on failure carries the reason
/// and, when the token was genuine but dead (expired/revoked), the recipient
/// email so the login page can pre-stage the PIN flow.
/// </summary>
public sealed record EmailMagicLinkResolution(
    bool Success,
    int? ParticipantId,
    string Reason,
    string? RecoveryEmail = null)
{
    public static EmailMagicLinkResolution Ok(int participantId) =>
        new(true, participantId, "Signed in.");

    public static EmailMagicLinkResolution Fail(string reason, string? recoveryEmail = null) =>
        new(false, null, reason, recoveryEmail);
}
