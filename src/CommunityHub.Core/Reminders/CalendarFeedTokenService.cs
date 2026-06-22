using System.Security.Cryptography;
using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Mints, resolves and regenerates the per-participant calendar-feed token
/// (<see cref="CommunityHub.Core.Domain.Participant.CalendarFeedToken"/>).
///
/// The token is a 256-bit cryptographically-random value, URL-safe base64
/// (no padding / no +/), so it is unguessable and safe to embed in a webcal://
/// URL a calendar client fetches without a session. Each participant has at
/// most one active token; regenerating it (revoke) replaces the value so any
/// previously-shared feed URL stops resolving.
/// </summary>
public sealed class CalendarFeedTokenService
{
    private readonly CommunityHubDbContext _db;
    private readonly IAuditTrail? _audit;

    public CalendarFeedTokenService(CommunityHubDbContext db, IAuditTrail? audit = null)
    {
        _db = db;
        _audit = audit;
    }

    /// <summary>
    /// Return the participant's token, minting one on first use. Idempotent:
    /// once a token exists it is returned unchanged on every later call.
    /// </summary>
    public async Task<string> EnsureTokenAsync(int participantId, CancellationToken ct = default)
    {
        var p = await _db.Participants.FirstOrDefaultAsync(x => x.Id == participantId, ct)
            ?? throw new InvalidOperationException($"Participant {participantId} not found.");

        if (!string.IsNullOrWhiteSpace(p.CalendarFeedToken))
        {
            return p.CalendarFeedToken!;
        }

        p.CalendarFeedToken = NewToken();
        await _db.SaveChangesAsync(ct);
        // AUDIT (§24): the FIRST mint is "this user set up calendar sync" — the usage
        // signal the organizer wants to count. (Per-poll feed fetches are NOT audited.)
        await AuditCalendarAsync(p, AuditActions.CalendarSubscribe, "Subscribed to calendar feed", ct);
        return p.CalendarFeedToken!;
    }

    /// <summary>
    /// Replace the participant's token (revoke + reissue). Any URL holding the
    /// old token immediately stops resolving. Returns the new token.
    /// </summary>
    public async Task<string> RegenerateTokenAsync(int participantId, CancellationToken ct = default)
    {
        var p = await _db.Participants.FirstOrDefaultAsync(x => x.Id == participantId, ct)
            ?? throw new InvalidOperationException($"Participant {participantId} not found.");

        p.CalendarFeedToken = NewToken();
        await _db.SaveChangesAsync(ct);
        await AuditCalendarAsync(p, AuditActions.CalendarTokenReset, "Reset calendar feed token", ct);
        return p.CalendarFeedToken!;
    }

    private async Task AuditCalendarAsync(Participant p, string action, string summary, CancellationToken ct)
    {
        if (_audit is null) return;
        await _audit.RecordAsync(new AuditEntry
        {
            EventId = p.EventId,
            Category = AuditCategory.CalendarSync,
            Action = action,
            ActorParticipantId = p.Id,
            ActorEmail = string.IsNullOrWhiteSpace(p.Email) ? "(unknown)" : p.Email,
            ActorRole = p.Role.ToString(),
            Summary = summary,
            TargetType = "CalendarFeed",
            TargetId = p.Id.ToString(),
        }, ct);
    }

    /// <summary>
    /// Resolve a presented token to its participant id, or null when the token is
    /// empty / unknown / belongs to a deactivated participant / belongs to an
    /// edition where the organizer has DISABLED calendar sync
    /// (<see cref="CommunityHub.Core.Domain.Event.CalendarSyncEnabled"/>). The
    /// unique index makes this an exact lookup. Returning null on a disabled
    /// edition makes the feed 404 — indistinguishable from a never-issued token.
    /// </summary>
    public async Task<int?> ResolveParticipantIdAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var trimmed = token.Trim();

        var id = await _db.Participants
            .AsNoTracking()
            .Where(p => p.CalendarFeedToken == trimmed
                        && p.IsActive
                        && p.Event.CalendarSyncEnabled)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
        return id;
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32); // 256 bits
        // URL-safe base64 without padding.
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
