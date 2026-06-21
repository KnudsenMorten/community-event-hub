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

    /// <summary>
    /// True only when the grant may still be redeemed at <paramref name="now"/>:
    /// not revoked, not already consumed, and not yet expired.
    /// </summary>
    public bool IsRedeemableAt(DateTimeOffset now) =>
        RevokedAt is null && ConsumedAt is null && now < ExpiresAt;
}
