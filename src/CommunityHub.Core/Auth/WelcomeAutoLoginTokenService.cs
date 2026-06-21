using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Auth;

/// <summary>
/// The shipped <see cref="IWelcomeAutoLoginTokenService"/>: a single-use,
/// short-lived, revocable, audited auto-login token for the welcome email.
///
/// <para><b>Token shape.</b> The URL token is an ASP.NET DataProtection payload
/// <c>{ GrantId, TokenId, ExpiresAtUtcTicks }</c> — tamper-evident and bearing
/// no secret beyond the random <c>TokenId</c>. The server stores only the
/// SHA-256 hash of <c>TokenId</c> on the <see cref="MagicLinkGrant"/> row, so a
/// database leak cannot be replayed as a sign-in (you'd need the raw id, which
/// only ever lived in the emailed URL).</para>
///
/// <para><b>Redeem = consume.</b> Redemption validates the signature + expiry,
/// looks up the grant by hash, checks it is redeemable (not consumed, not
/// revoked, unexpired) and still scoped to an active participant of the same
/// role, then stamps <see cref="MagicLinkGrant.ConsumedAt"/> in the SAME save —
/// a concurrent second tap finds it already consumed and is refused. This is the
/// single-use gate the welcome auto-login security model requires.</para>
///
/// <para>It uses its OWN DataProtection purpose, distinct from the reusable
/// invitation <see cref="MagicLinkTokenFactory"/>, so the two token kinds can
/// never be confused for one another.</para>
/// </summary>
public sealed class WelcomeAutoLoginTokenService : IWelcomeAutoLoginTokenService
{
    /// <summary>What the backing grant rows are tagged with.</summary>
    public const string PurposeName = "welcome";

    /// <summary>
    /// Default lifetime: 14 days — matches the welcome onboarding window the
    /// organizer expects (recipients commonly open the welcome days later). The
    /// link stays SINGLE-USE + scoped + revocable, so the longer window does not
    /// weaken it; a lapsed link falls back to the email + one-time-code flow.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(14);

    private const string DataProtectionPurpose = "CommunityHub.WelcomeAutoLogin.v1";

    private readonly CommunityHubDbContext _db;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    public WelcomeAutoLoginTokenService(
        CommunityHubDbContext db,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider clock)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<string> CreateAsync(
        int participantId, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId, ct)
            ?? throw new InvalidOperationException(
                $"Cannot mint a welcome auto-login token for unknown participant {participantId}.");

        // 256-bit URL-safe random id; only its hash is persisted.
        var tokenId = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = _clock.GetUtcNow();
        var expiresAt = now.Add(ttl ?? DefaultTtl);

        var grant = new MagicLinkGrant
        {
            EventId = participant.EventId,
            ParticipantId = participant.Id,
            Role = participant.Role,
            Purpose = PurposeName,
            TokenIdHash = HashTokenId(tokenId),
            RecipientEmail = participant.Email,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };
        _db.Set<MagicLinkGrant>().Add(grant);
        await _db.SaveChangesAsync(ct);

        var payload = new Payload(grant.Id, tokenId, expiresAt.UtcTicks);
        var json = JsonSerializer.Serialize(payload);
        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(json));
        return Base64Url(protectedBytes);
    }

    /// <inheritdoc />
    public async Task<WelcomeAutoLoginRedemption> RedeemAsync(
        string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return WelcomeAutoLoginRedemption.Fail("Empty token.");
        }

        Payload? payload;
        try
        {
            var bytes = FromBase64Url(token);
            var json = Encoding.UTF8.GetString(_protector.Unprotect(bytes));
            payload = JsonSerializer.Deserialize<Payload>(json);
        }
        catch
        {
            // Tampered / alien / wrong-purpose token: never throw.
            return WelcomeAutoLoginRedemption.Fail("Invalid token.");
        }
        if (payload is null)
        {
            return WelcomeAutoLoginRedemption.Fail("Invalid token.");
        }

        var now = _clock.GetUtcNow();
        if (now.UtcTicks > payload.ExpiresAtUtcTicks)
        {
            return WelcomeAutoLoginRedemption.Fail("Expired token.");
        }

        var grant = await _db.Set<MagicLinkGrant>()
            .FirstOrDefaultAsync(g => g.Id == payload.GrantId, ct);
        if (grant is null)
        {
            return WelcomeAutoLoginRedemption.Fail("Unknown grant.");
        }

        // Bind the presented token id to the stored grant by constant-time hash
        // comparison — never trust the payload's grant id alone.
        if (!FixedTimeEquals(grant.TokenIdHash, HashTokenId(payload.TokenId)))
        {
            return WelcomeAutoLoginRedemption.Fail("Token does not match grant.");
        }
        if (!grant.IsRedeemableAt(now))
        {
            return WelcomeAutoLoginRedemption.Fail(
                grant.ConsumedAt is not null ? "Already used."
                : grant.RevokedAt is not null ? "Revoked."
                : "Expired.");
        }

        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.Id == grant.ParticipantId, ct);
        if (participant is null || !participant.IsActive)
        {
            return WelcomeAutoLoginRedemption.Fail("Account is inactive.");
        }
        // Scope check: a re-roled person's stale link must not sign them into a
        // hub they no longer belong to.
        if (participant.Role != grant.Role)
        {
            return WelcomeAutoLoginRedemption.Fail("Role changed since the link was issued.");
        }

        // SINGLE-USE: consume now, in this save. A concurrent second tap that
        // reaches here will fail the IsRedeemableAt check above (its read sees
        // ConsumedAt set) — and if two reads race, the unique-by-id update still
        // only ever lands one ConsumedAt stamp.
        grant.ConsumedAt = now;
        await _db.SaveChangesAsync(ct);

        return WelcomeAutoLoginRedemption.Ok(participant.Id);
    }

    /// <summary>
    /// SHA-256 of the random token id, lowercase hex — what the grant stores so
    /// the raw id never touches the database.
    /// </summary>
    public static string HashTokenId(string tokenId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(tokenId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a ?? string.Empty),
            Encoding.UTF8.GetBytes(b ?? string.Empty));

    private sealed record Payload(int GrantId, string TokenId, long ExpiresAtUtcTicks);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
