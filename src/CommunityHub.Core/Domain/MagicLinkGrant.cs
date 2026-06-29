namespace CommunityHub.Core.Domain;

/// <summary>
/// Server-side record (and audit trail) for a <b>welcome auto-login</b>
/// magic-link — the one-tap sign-in CTA in the welcome email. It exists so that
/// the welcome link, unlike the reusable invitation magic-link, is
/// <b>single-use</b>, <b>short-lived</b>, <b>scoped to exactly one person+role
/// in one edition</b>, and <b>revocable</b> — the security model required for an
/// email that signs the recipient in with no password or code.
///
/// <para>The grant stores only a <b>SHA-256 hash</b> of the random token id, not
/// the token itself: a database leak cannot be replayed as a sign-in. The opaque
/// token in the URL is a DataProtection-signed payload that carries the grant id
/// (a 256-bit random value); validating it both proves the link was minted by us
/// (signature) and resolves to exactly this grant (the hash lookup).</para>
///
/// <para>Modelled on <see cref="ParticipantSecretaryToken"/> (single-person
/// scope, time-bound, revocable, audited), with the addition of
/// <see cref="ConsumedAt"/> for true <b>single-use</b> semantics.</para>
/// </summary>
public class MagicLinkGrant
{
    public int Id { get; set; }

    /// <summary>Edition scope — every lookup is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The ONE participant this link signs in (single-person scope).</summary>
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>
    /// The role the link is scoped to, captured at mint time. The sign-in is
    /// refused if the participant's role no longer matches (defence in depth:
    /// a re-roled person's stale welcome link won't sign them into a hub they
    /// no longer belong to).
    /// </summary>
    public ParticipantRole Role { get; set; }

    /// <summary>
    /// What the grant is for, so a single table can back more than one
    /// single-use link kind later. Today the only value is <c>"welcome"</c>.
    /// </summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>
    /// The <b>SHA-256 hash</b> (lowercase hex) of the random token id embedded
    /// in the signed URL token. The raw id is never stored, so a DB read cannot
    /// reconstruct a usable link. Unique so a presented token resolves to one
    /// grant.
    /// </summary>
    public string TokenIdHash { get; set; } = string.Empty;

    /// <summary>Email the grant was minted for (audit; the identity address).</summary>
    public string RecipientEmail { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The grant is unusable on/after this instant (short-lived).</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Set the first time the link is successfully used. A second presentation
    /// finds it non-null and is refused — this is the <b>single-use</b> gate.
    /// </summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>Set when the grant is revoked; null = still usable (subject to expiry/consumption).</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    // --- §169 personal email magic-link (long-lived, MULTI-USE) ---------------
    // The columns below are added (all nullable / safe-defaulted) so the SAME
    // table can also back the §169 email auto-login link: a per-participant,
    // per-edition, 365-day, REUSABLE sign-in token carried in every in-hub email
    // CTA. Such grants set <see cref="MultiUse"/> = true and
    // <see cref="Purpose"/> = "email"; they NEVER set <see cref="ConsumedAt"/>
    // (reuse is the whole point), so usage is tracked by
    // <see cref="UseCount"/> + <see cref="LastUsedAt"/> instead. Legacy
    // single-use "welcome" grants leave all of these at their defaults, so their
    // behaviour is byte-for-byte unchanged.

    /// <summary>
    /// True for a REUSABLE link (the §169 email magic-link): redemption does NOT
    /// consume it; it stays redeemable until it expires or is revoked. False (the
    /// default) keeps the original SINGLE-USE welcome semantics.
    /// </summary>
    public bool MultiUse { get; set; }

    /// <summary>
    /// DataProtection-encrypted copy of the random token, kept ONLY so the same
    /// reusable link can be re-embedded across that participant's many emails
    /// without minting a new one each time. The cleartext token still lives only
    /// in the emailed URL: this column is ciphertext (decryptable only with the
    /// app's DataProtection key ring), and the redeem path matches by
    /// <see cref="TokenIdHash"/>, never by decrypting this. Null for the
    /// single-use welcome grants (which are never re-embedded).
    /// </summary>
    public string? TokenProtected { get; set; }

    /// <summary>Last time a (multi-use) link successfully signed someone in — abuse visibility.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>How many times this link has successfully signed someone in (multi-use logging).</summary>
    public int UseCount { get; set; }

    /// <summary>
    /// True only when the grant may still be redeemed at <paramref name="now"/>:
    /// not revoked, not already consumed, and not yet expired. A multi-use grant
    /// never sets <see cref="ConsumedAt"/>, so the consumed check is a no-op for
    /// it and it stays redeemable for its whole (1-year) lifetime.
    /// </summary>
    public bool IsRedeemableAt(DateTimeOffset now) =>
        RevokedAt is null && ConsumedAt is null && now < ExpiresAt;
}
