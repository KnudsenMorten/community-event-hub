namespace CommunityHub.Core.Auth;

/// <summary>
/// Mints + redeems the <b>hardened, single-use</b> auto-login token used by the
/// welcome email's one-tap sign-in CTA. It is deliberately stronger than the
/// reusable invitation magic-link (<see cref="IMagicLinkTokenFactory"/>):
///
/// <list type="bullet">
///   <item><b>Single-use</b> — the first redemption marks the backing grant
///     consumed; a second presentation is refused.</item>
///   <item><b>Short-lived</b> — a short default TTL (the welcome is an
///     onboarding nudge, not a standing credential).</item>
///   <item><b>Cryptographically signed + random</b> — the URL token is a
///     DataProtection-signed payload carrying a 256-bit random id; the server
///     stores only the id's SHA-256 hash, so a DB leak can't be replayed.</item>
///   <item><b>Scoped</b> — to exactly one participant + role in one edition.</item>
///   <item><b>Revocable + auditable</b> — backed by a <c>MagicLinkGrant</c> row
///     the organizer can revoke; consumption + revocation are recorded.</item>
/// </list>
///
/// Lives in Core so the welcome email (also Core) can mint a link without
/// depending on the web project; the web <c>/Login/Magic</c> handler redeems it.
/// </summary>
public interface IWelcomeAutoLoginTokenService
{
    /// <summary>
    /// Mint a single-use auto-login token for one participant, creating its
    /// backing grant. A null <paramref name="ttl"/> uses the default lifetime.
    /// Returns the opaque, URL-safe token to embed in the welcome link.
    /// </summary>
    Task<string> CreateAsync(
        int participantId, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>
    /// Redeem a presented token: validate the signature, expiry, scope and the
    /// backing grant, and — on success — mark the grant <b>consumed</b> so it can
    /// never be reused. Returns the resolved sign-in details, or
    /// <see cref="WelcomeAutoLoginRedemption.Failed"/> on any failure (tampered,
    /// expired, already used, revoked, inactive, role-changed). Never throws.
    /// </summary>
    Task<WelcomeAutoLoginRedemption> RedeemAsync(
        string token, CancellationToken ct = default);
}

/// <summary>
/// The outcome of redeeming a welcome auto-login token. On success carries the
/// participant the session should be established for; on failure carries only
/// the reason (no participant), so the landing page can show a recovery state.
/// </summary>
public sealed record WelcomeAutoLoginRedemption(
    bool Success,
    int? ParticipantId,
    string Reason)
{
    public static WelcomeAutoLoginRedemption Ok(int participantId) =>
        new(true, participantId, "Redeemed.");

    public static WelcomeAutoLoginRedemption Fail(string reason) =>
        new(false, null, reason);
}
