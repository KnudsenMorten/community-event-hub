namespace CommunityHub.Core.Domain;

/// <summary>
/// A time-bound, revocable secure-token grant that lets a <b>secretary</b>
/// (e.g. the assistant of a VP or a speaker who delegates their admin) sign in
/// scoped to <b>exactly one</b> participant and fill in that person's
/// onboarding / tasks <i>on their behalf</i>.
///
/// It mirrors the calendar-feed token pattern (an unguessable 256-bit URL-safe
/// bearer secret) but is <b>write-scoped</b> and far more constrained:
///   - <b>single-person scope</b> — the token resolves to exactly this
///     <see cref="ParticipantId"/>; the secretary session can only ever touch
///     that participant's own data;
///   - <b>time-bound</b> — unusable on/after <see cref="ExpiresAt"/>;
///   - <b>revocable</b> — set <see cref="RevokedAt"/> (or regenerate the row's
///     token) and the link stops resolving immediately.
/// A secretary session is NOT an organizer: it can never reach organizer-only
/// areas and can never start an impersonation.
/// </summary>
public class ParticipantSecretaryToken
{
    public int Id { get; set; }

    /// <summary>Edition scope — every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The ONE participant this token acts for (single-person scope).</summary>
    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>
    /// The 256-bit cryptographically-random, URL-safe bearer secret embedded in
    /// the secretary URL (<c>/s/{token}</c>). Stored unique so a presented token
    /// resolves to exactly one grant.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Optional free-text label for the organizer (e.g. "VP assistant").</summary>
    public string? Label { get; set; }

    /// <summary>Email of the organizer who issued the grant (audit).</summary>
    public string? IssuedByEmail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The grant is unusable on/after this instant.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the grant is revoked; null = still valid (subject to expiry).</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Last time the link was successfully used (audit; optional).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Valid = not revoked AND not yet expired at <paramref name="now"/>.</summary>
    public bool IsValidAt(DateTimeOffset now) =>
        RevokedAt is null && now < ExpiresAt;
}
