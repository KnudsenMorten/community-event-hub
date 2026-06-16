using System.Security.Cryptography;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// Issues, lists, resolves and revokes <see cref="ParticipantSecretaryToken"/>
/// grants — the write-scoped sibling of the calendar-feed token. A grant lets a
/// secretary sign in scoped to exactly ONE participant to fill in that person's
/// onboarding/tasks on their behalf.
///
/// Security shape (mirrors CalendarFeedTokenService for the secret, adds the
/// constraints a write-scoped grant needs):
///   - 256-bit cryptographically-random, URL-safe token (unguessable);
///   - <b>time-bound</b>: <see cref="IssueAsync"/> sets an explicit expiry;
///   - <b>revocable</b>: <see cref="RevokeAsync"/> stamps RevokedAt so the link
///     stops resolving immediately;
///   - <b>single-person scope</b>: <see cref="ResolveAsync"/> returns the ONE
///     valid grant a token maps to (active participant only), or null.
/// </summary>
public sealed class SecretaryTokenService
{
    /// <summary>Default grant lifetime when the caller does not specify one.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SecretaryTokenService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Issue a new grant for a participant (edition-scoped). Returns the created
    /// row (with its fresh <see cref="ParticipantSecretaryToken.Token"/>), or
    /// null if the participant does not exist in this edition. A participant can
    /// hold several grants; revoke individually.
    /// </summary>
    public async Task<ParticipantSecretaryToken?> IssueAsync(
        int eventId, int participantId, string? label, string? issuedByEmail,
        TimeSpan? lifetime = null, CancellationToken ct = default)
    {
        var exists = await _db.Participants
            .AnyAsync(p => p.Id == participantId && p.EventId == eventId, ct);
        if (!exists) return null;

        var now = _clock.GetUtcNow();
        var grant = new ParticipantSecretaryToken
        {
            EventId = eventId,
            ParticipantId = participantId,
            Token = NewToken(),
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            IssuedByEmail = string.IsNullOrWhiteSpace(issuedByEmail) ? null : issuedByEmail.Trim(),
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime ?? DefaultLifetime),
        };
        _db.ParticipantSecretaryTokens.Add(grant);
        await _db.SaveChangesAsync(ct);
        return grant;
    }

    /// <summary>All grants for one participant in an edition, newest first.</summary>
    public async Task<IReadOnlyList<ParticipantSecretaryToken>> ListForParticipantAsync(
        int eventId, int participantId, CancellationToken ct = default)
        => await _db.ParticipantSecretaryTokens
            .Where(t => t.EventId == eventId && t.ParticipantId == participantId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Revoke one grant (edition-scoped). Idempotent: a re-revoke keeps the
    /// first timestamp. Returns false if the id is not a grant of this edition.
    /// </summary>
    public async Task<bool> RevokeAsync(
        int eventId, int tokenId, CancellationToken ct = default)
    {
        var grant = await _db.ParticipantSecretaryTokens.FirstOrDefaultAsync(
            t => t.Id == tokenId && t.EventId == eventId, ct);
        if (grant is null) return false;
        if (grant.RevokedAt is null)
        {
            grant.RevokedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        return true;
    }

    /// <summary>
    /// Resolve a presented token to its VALID grant: not revoked, not expired,
    /// and pointing at an <b>active</b> participant (lifecycle-correct
    /// <c>IsActive AND LifecycleState == Active</c>, so a withdrawn / not-yet-
    /// activated person's link never works). Returns null otherwise. Stamps
    /// <see cref="ParticipantSecretaryToken.LastUsedAt"/> on a successful use.
    /// </summary>
    public async Task<ParticipantSecretaryToken?> ResolveAsync(
        string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var trimmed = token.Trim();
        var now = _clock.GetUtcNow();

        var grant = await _db.ParticipantSecretaryTokens
            .Include(t => t.Participant)
            .FirstOrDefaultAsync(t => t.Token == trimmed, ct);

        if (grant is null) return null;
        if (!grant.IsValidAt(now)) return null;
        if (grant.Participant is null || !ParticipantActivation.IsActive(grant.Participant))
        {
            return null;
        }

        grant.LastUsedAt = now;
        await _db.SaveChangesAsync(ct);
        return grant;
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32); // 256 bits
        // URL-safe base64 without padding (same shape as the calendar-feed token).
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
