using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Auth;

/// <summary>
/// The shipped <see cref="IEmailMagicLinkService"/> (REQUIREMENTS §169).
///
/// <para><b>Token + storage.</b> The URL token is a 256-bit cryptographically
/// random, URL-safe value. The grant row stores only its <b>SHA-256 hash</b>
/// (<see cref="MagicLinkGrant.TokenIdHash"/>, a unique index) so a presented
/// token resolves in one constant-time lookup and a database read of the grants
/// table alone cannot be replayed as a sign-in. The cleartext token lives in the
/// emailed URL; a DataProtection-encrypted copy
/// (<see cref="MagicLinkGrant.TokenProtected"/>) is kept ONLY so the SAME
/// reusable link can be re-embedded across that participant's many emails without
/// minting a new one each time — it is ciphertext (decryptable only with the
/// app's key ring, the same root of trust that protects the session cookie), and
/// redemption matches by hash, never by decrypting it.</para>
///
/// <para><b>Reuse.</b> <see cref="GetOrCreateTokenAsync"/> reuses the participant's
/// live multi-use grant (decrypting <c>TokenProtected</c> to return the identical
/// token) so two builds for the same person yield the same link. Only when none
/// exists is a fresh one minted.</para>
///
/// <para><b>Redeem ≠ consume.</b> The grant is <see cref="MagicLinkGrant.MultiUse"/>
/// = true and is never marked consumed; redemption only stamps
/// <see cref="MagicLinkGrant.LastUsedAt"/> + bumps <see cref="MagicLinkGrant.UseCount"/>
/// and writes an audit entry. The grant stays usable for its full 365-day life
/// until it expires or the organizer revokes it.</para>
///
/// <para>It uses its OWN DataProtection purpose and its OWN grant
/// <see cref="MagicLinkGrant.Purpose"/> tag (<c>"email"</c>), so it can never be
/// confused with the single-use welcome auto-login grants in the same table.</para>
/// </summary>
public sealed class EmailMagicLinkService : IEmailMagicLinkService
{
    /// <summary>The grant <see cref="MagicLinkGrant.Purpose"/> tag for these links.</summary>
    public const string PurposeName = "email";

    /// <summary>1-year lifetime (REQUIREMENTS §169 "valid for 1 year").</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(365);

    private const string DataProtectionPurpose = "CommunityHub.EmailMagicLink.v1";

    private readonly CommunityHubDbContext _db;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly IAuditTrail? _audit;

    public EmailMagicLinkService(
        CommunityHubDbContext db,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider clock,
        IAuditTrail? audit = null)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
        _clock = clock;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<string> GetOrCreateTokenAsync(int participantId, CancellationToken ct = default)
    {
        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId, ct)
            ?? throw new InvalidOperationException(
                $"Cannot mint an email magic-link for unknown participant {participantId}.");

        var now = _clock.GetUtcNow();

        // Reuse the live grant so every email carries the SAME link (one token per
        // participant). Pick the newest still-redeemable one; decrypt its stored
        // ciphertext to recover the original token.
        var live = await _db.MagicLinkGrants
            .Where(g => g.ParticipantId == participantId
                        && g.Purpose == PurposeName
                        && g.MultiUse
                        && g.RevokedAt == null
                        && g.ExpiresAt > now
                        && g.TokenProtected != null)
            .OrderByDescending(g => g.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (live is not null)
        {
            var recovered = TryUnprotect(live.TokenProtected!);
            if (recovered is not null) return recovered;
            // Ciphertext somehow unreadable (e.g. key rotated away): fall through and
            // mint a fresh grant rather than fail the email send.
        }

        var token = NewToken();
        var grant = new MagicLinkGrant
        {
            EventId = participant.EventId,
            ParticipantId = participant.Id,
            Role = participant.Role,
            Purpose = PurposeName,
            MultiUse = true,
            TokenIdHash = HashToken(token),
            TokenProtected = _protector.Protect(token),
            RecipientEmail = participant.Email,
            CreatedAt = now,
            ExpiresAt = now.Add(DefaultTtl),
        };
        _db.MagicLinkGrants.Add(grant);
        await _db.SaveChangesAsync(ct);
        return token;
    }

    /// <inheritdoc />
    public string BuildUrl(string baseUrl, string token, string? localPath = null)
    {
        var origin = (baseUrl ?? string.Empty).TrimEnd('/');
        var url = $"{origin}/go/{token}";
        var safe = SafeLocalPath(localPath);
        return safe is null ? url : $"{url}?r={Uri.EscapeDataString(safe)}";
    }

    /// <inheritdoc />
    public async Task<string> BuildUrlForParticipantAsync(
        int participantId, string baseUrl, string? localPath = null, CancellationToken ct = default)
    {
        var token = await GetOrCreateTokenAsync(participantId, ct);
        return BuildUrl(baseUrl, token, localPath);
    }

    /// <inheritdoc />
    public async Task<EmailMagicLinkResolution> ResolveAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return EmailMagicLinkResolution.Fail("Empty token.");
        }

        var hash = HashToken(token.Trim());
        var grant = await _db.MagicLinkGrants
            .FirstOrDefaultAsync(g => g.TokenIdHash == hash && g.Purpose == PurposeName, ct);
        if (grant is null)
        {
            // Unknown / alien / tampered token → no recovery email to pre-stage.
            return EmailMagicLinkResolution.Fail("Unknown link.");
        }

        var now = _clock.GetUtcNow();
        if (!grant.IsRedeemableAt(now))
        {
            // Genuine but dead (expired/revoked): hand back the recipient email so
            // the login page can pre-stage the PIN flow in one tap.
            var reason = grant.RevokedAt is not null ? "Revoked." : "Expired.";
            return EmailMagicLinkResolution.Fail(reason, grant.RecipientEmail);
        }

        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.Id == grant.ParticipantId, ct);
        if (participant is null || !participant.IsActive)
        {
            // No pre-fill: the PIN flow would also refuse an inactive account, so we
            // don't imply it will work.
            return EmailMagicLinkResolution.Fail("Account is inactive.");
        }
        // Bound to participant + edition (REQUIREMENTS §169). The token never signs
        // anyone into an edition it was not minted for. (Role is intentionally NOT
        // re-checked: a 1-year link follows the person; they sign in with their
        // CURRENT role.)
        if (participant.EventId != grant.EventId)
        {
            return EmailMagicLinkResolution.Fail("Edition mismatch.");
        }

        // Reusable: record usage, never consume.
        grant.LastUsedAt = now;
        grant.UseCount += 1;
        await _db.SaveChangesAsync(ct);

        await AuditAsync(participant, grant, ct);
        return EmailMagicLinkResolution.Ok(participant.Id);
    }

    private async Task AuditAsync(Participant p, MagicLinkGrant grant, CancellationToken ct)
    {
        if (_audit is null) return;
        await _audit.RecordAsync(new AuditEntry
        {
            EventId = p.EventId,
            Category = AuditCategory.Auth,
            Action = AuditActions.MagicLink,
            ActorParticipantId = p.Id,
            ActorEmail = string.IsNullOrWhiteSpace(p.Email) ? "(unknown)" : p.Email,
            ActorRole = p.Role.ToString(),
            Summary = $"Signed in via personal email magic-link (use #{grant.UseCount}).",
            TargetType = "MagicLinkGrant",
            TargetId = grant.Id.ToString(),
            Source = AuditSource.Web,
        }, ct);
    }

    // --- organizer admin (WelcomeLinks page) ---------------------------------

    public enum LinkState { Active, Revoked, Expired }

    /// <summary>One row for the organizer admin: a participant's email magic-link + its usage.</summary>
    public sealed record LinkRow(
        int Id,
        int ParticipantId,
        string RecipientEmail,
        string FullName,
        ParticipantRole Role,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? LastUsedAt,
        int UseCount,
        DateTimeOffset? RevokedAt,
        LinkState State)
    {
        /// <summary>Active links can be revoked; active OR dead links can be rotated (re-issued).</summary>
        public bool CanRevoke => State == LinkState.Active;
    }

    /// <summary>
    /// List the edition's personal email magic-links (newest first) with each
    /// link's status + usage (issued / last used / use-count / expires) for the
    /// organizer admin. Edition-scoped.
    /// </summary>
    public async Task<IReadOnlyList<LinkRow>> ListAsync(
        int eventId, DateTimeOffset now, CancellationToken ct = default)
    {
        var grants = await _db.MagicLinkGrants
            .AsNoTracking()
            .Where(g => g.EventId == eventId && g.Purpose == PurposeName)
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new
            {
                g.Id, g.ParticipantId, g.RecipientEmail, g.Role, g.CreatedAt,
                g.ExpiresAt, g.LastUsedAt, g.UseCount, g.RevokedAt,
                FullName = g.Participant.FullName,
            })
            .ToListAsync(ct);

        return grants.Select(g =>
        {
            var state =
                g.RevokedAt is not null ? LinkState.Revoked
                : now >= g.ExpiresAt ? LinkState.Expired
                : LinkState.Active;
            return new LinkRow(g.Id, g.ParticipantId, g.RecipientEmail, g.FullName,
                g.Role, g.CreatedAt, g.ExpiresAt, g.LastUsedAt, g.UseCount,
                g.RevokedAt, state);
        }).ToList();
    }

    /// <summary>
    /// Revoke one email magic-link. Edition-scoped + idempotent: returns false for
    /// an unknown / wrong-edition / already-revoked grant. Stamps
    /// <see cref="MagicLinkGrant.RevokedAt"/> so the next <see cref="ResolveAsync"/>
    /// is refused.
    /// </summary>
    public async Task<bool> RevokeAsync(
        int eventId, int grantId, DateTimeOffset now, CancellationToken ct = default)
    {
        var grant = await _db.MagicLinkGrants
            .FirstOrDefaultAsync(g => g.Id == grantId && g.EventId == eventId
                && g.Purpose == PurposeName, ct);
        if (grant is null || grant.RevokedAt is not null) return false;

        grant.RevokedAt = now;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Rotate a participant's email magic-link: revoke EVERY live link of theirs
    /// (so the old emailed URL stops working) and mint a fresh one. Edition-scoped;
    /// returns the new token, or null for an unknown / wrong-edition grant. The
    /// grant id identifies the participant to rotate.
    /// </summary>
    public async Task<string?> RotateAsync(
        int eventId, int grantId, DateTimeOffset now, CancellationToken ct = default)
    {
        var seed = await _db.MagicLinkGrants
            .FirstOrDefaultAsync(g => g.Id == grantId && g.EventId == eventId
                && g.Purpose == PurposeName, ct);
        if (seed is null) return null;

        var live = await _db.MagicLinkGrants
            .Where(g => g.ParticipantId == seed.ParticipantId
                        && g.Purpose == PurposeName
                        && g.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var g in live) g.RevokedAt = now;
        await _db.SaveChangesAsync(ct);

        // Mint a fresh standing link for the participant.
        return await GetOrCreateTokenAsync(seed.ParticipantId, ct);
    }

    /// <summary>SHA-256 of the token, lowercase hex — what the grant stores so the raw never touches the DB.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Honour only local ("/"-prefixed, non-protocol-relative) deep-link targets — mirrors SafeLocalReturnUrl.</summary>
    public static string? SafeLocalPath(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.StartsWith('/') && !url.StartsWith("//")
            ? url
            : null;

    private string? TryUnprotect(string protectedToken)
    {
        try { return _protector.Unprotect(protectedToken); }
        catch { return null; }
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32); // 256 bits
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
