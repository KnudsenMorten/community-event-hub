namespace CommunityHub.Core.Domain;

/// <summary>
/// A single emailed login PIN. Per CONTEXT.md section 5: the app generates a
/// 6-digit PIN, stores only its <b>hash</b> with a 15-minute expiry, emails
/// the plaintext, and never logs the plaintext. PINs are single-use.
/// </summary>
public class LoginPin
{
    public int Id { get; set; }

    // --- Who the PIN is for -------------------------------------------------
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>
    /// Hash of the 6-digit PIN (e.g. a salted SHA-256 / PBKDF2 digest).
    /// The plaintext PIN is NEVER stored or logged.
    /// </summary>
    public string PinHash { get; set; } = string.Empty;

    /// <summary>When the PIN was issued.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>15 minutes after CreatedAt. After this the PIN is rejected.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Set when the PIN is successfully redeemed. A non-null value means the
    /// PIN is spent and cannot be used again (single-use).
    /// </summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>
    /// Count of failed match attempts against this PIN - lets the verifier
    /// lock a PIN after a few wrong tries without affecting other PINs.
    /// </summary>
    public int FailedAttempts { get; set; }

    /// <summary>True only while unconsumed and not past expiry.</summary>
    public bool IsRedeemable(DateTimeOffset now) =>
        ConsumedAt is null && now < ExpiresAt;
}
