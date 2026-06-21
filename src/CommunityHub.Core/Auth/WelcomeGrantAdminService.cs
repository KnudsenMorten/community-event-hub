using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Auth;

/// <summary>
/// Organizer-facing administration + housekeeping for welcome auto-login grants
/// (<see cref="MagicLinkGrant"/>). The token <see cref="WelcomeAutoLoginTokenService"/>
/// mints + redeems; this service is the read/revoke/prune side the security model
/// called for (REQUIREMENTS §4 "Remaining: organizer-facing revoke UI for issued
/// grants; optional periodic prune of expired/consumed rows").
///
/// All reads + writes are <b>edition-scoped</b>. Revoke is idempotent and never
/// "un-consumes" a used link; prune only removes grants that can no longer be
/// redeemed AND are older than a retention window, so the recent audit trail
/// (who got a link, was it used) survives.
/// </summary>
public sealed class WelcomeGrantAdminService
{
    private readonly CommunityHubDbContext _db;

    /// <summary>Default audit-retention window before a dead grant is pruned.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    public WelcomeGrantAdminService(CommunityHubDbContext db) => _db = db;

    public enum GrantState { Active, Consumed, Revoked, Expired }

    public sealed record GrantRow(
        int Id,
        string RecipientEmail,
        string FullName,
        ParticipantRole Role,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ConsumedAt,
        DateTimeOffset? RevokedAt,
        GrantState State)
    {
        public bool CanRevoke => State == GrantState.Active;
    }

    /// <summary>
    /// Compute the display state of a grant at <paramref name="now"/>. Revoked +
    /// consumed are terminal facts; "expired" is derived from the clock.
    /// </summary>
    public static GrantState StateOf(MagicLinkGrant g, DateTimeOffset now) =>
        g.RevokedAt is not null ? GrantState.Revoked
        : g.ConsumedAt is not null ? GrantState.Consumed
        : now >= g.ExpiresAt ? GrantState.Expired
        : GrantState.Active;

    /// <summary>
    /// List welcome grants for an edition, newest first. When
    /// <paramref name="activeOnly"/> is true only still-redeemable grants are
    /// returned (the common "what can I revoke?" view).
    /// </summary>
    public async Task<IReadOnlyList<GrantRow>> ListAsync(
        int eventId, DateTimeOffset now, bool activeOnly = false,
        CancellationToken ct = default)
    {
        var grants = await _db.MagicLinkGrants
            .AsNoTracking()
            .Where(g => g.EventId == eventId
                        && g.Purpose == WelcomeAutoLoginTokenService.PurposeName)
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new
            {
                g.Id, g.RecipientEmail, g.Role, g.CreatedAt, g.ExpiresAt,
                g.ConsumedAt, g.RevokedAt,
                FullName = g.Participant.FullName,
            })
            .ToListAsync(ct);

        var rows = grants.Select(g =>
        {
            var state =
                g.RevokedAt is not null ? GrantState.Revoked
                : g.ConsumedAt is not null ? GrantState.Consumed
                : now >= g.ExpiresAt ? GrantState.Expired
                : GrantState.Active;
            return new GrantRow(g.Id, g.RecipientEmail, g.FullName, g.Role,
                g.CreatedAt, g.ExpiresAt, g.ConsumedAt, g.RevokedAt, state);
        });

        if (activeOnly) rows = rows.Where(r => r.State == GrantState.Active);
        return rows.ToList();
    }

    /// <summary>
    /// Revoke one grant. Edition-scoped, idempotent: returns false if the grant
    /// is unknown to this edition or is already consumed/revoked (a used link is
    /// never "un-consumed"; an already-revoked one is a no-op). Stamps
    /// <see cref="MagicLinkGrant.RevokedAt"/> so the next redeem is refused.
    /// </summary>
    public async Task<bool> RevokeAsync(
        int eventId, int grantId, DateTimeOffset now, CancellationToken ct = default)
    {
        var grant = await _db.MagicLinkGrants
            .FirstOrDefaultAsync(g => g.Id == grantId && g.EventId == eventId
                && g.Purpose == WelcomeAutoLoginTokenService.PurposeName, ct);
        if (grant is null || grant.ConsumedAt is not null || grant.RevokedAt is not null)
            return false;

        grant.RevokedAt = now;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Delete grants that can no longer be redeemed (consumed, revoked, OR
    /// expired at <paramref name="now"/>) AND were created before
    /// <c>now - retention</c>, so recent history is kept for audit. Active grants
    /// are never touched. Returns the number removed. <paramref name="eventId"/>
    /// null = all editions (the scheduled job's mode).
    /// </summary>
    public async Task<int> PruneAsync(
        DateTimeOffset now, TimeSpan? retention = null, int? eventId = null,
        CancellationToken ct = default)
    {
        var cutoff = now - (retention ?? DefaultRetention);
        var dead = await _db.MagicLinkGrants
            .Where(g => g.Purpose == WelcomeAutoLoginTokenService.PurposeName
                        && (eventId == null || g.EventId == eventId)
                        && g.CreatedAt < cutoff
                        && (g.ConsumedAt != null || g.RevokedAt != null
                            || g.ExpiresAt <= now))
            .ToListAsync(ct);
        if (dead.Count == 0) return 0;

        _db.MagicLinkGrants.RemoveRange(dead);
        await _db.SaveChangesAsync(ct);
        return dead.Count;
    }
}
