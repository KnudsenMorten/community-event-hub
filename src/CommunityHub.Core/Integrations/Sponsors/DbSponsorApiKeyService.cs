using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sponsors;

/// <summary>
/// Persistent implementation of <see cref="ISponsorApiKeyService"/> over
/// DbSet&lt;SponsorApiKey&gt; — the successor the in-memory scaffold's
/// comment promised. Same hashing scheme and validation surface; keys now
/// survive App Service restarts and slot swaps.
///
/// History is preserved: Issue revokes the previous active row and INSERTS
/// a new one (no updates-in-place of the hash), so the admin page can show
/// when keys were rotated and by whom.
/// </summary>
public sealed class DbSponsorApiKeyService : ISponsorApiKeyService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public DbSponsorApiKeyService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<(string rawKey, SponsorApiKey row)> IssueAsync(
        int eventId, string sponsorCompanyId, string? issuedByEmail, string? label, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // Revoke any previous active key for the pair (rotate semantics).
        var actives = await _db.SponsorApiKeys
            .Where(k => k.EventId == eventId
                        && k.SponsorCompanyId == sponsorCompanyId
                        && k.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var k in actives) { k.RevokedAt = now; }

        var raw = RandomHex(32);
        var row = new SponsorApiKey
        {
            EventId          = eventId,
            SponsorCompanyId = sponsorCompanyId,
            KeyHash          = Sha256Hex(raw),
            KeyPrefix        = raw[..8],
            Label            = label,
            IssuedAt         = now,
            IssuedByEmail    = issuedByEmail,
        };
        _db.SponsorApiKeys.Add(row);
        await _db.SaveChangesAsync(ct);
        return (raw, row);
    }

    public async Task<SponsorApiKey?> GetCurrentAsync(int eventId, string sponsorCompanyId, CancellationToken ct)
    {
        return await _db.SponsorApiKeys
            .Where(k => k.EventId == eventId
                        && k.SponsorCompanyId == sponsorCompanyId
                        && k.RevokedAt == null)
            .OrderByDescending(k => k.IssuedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task RevokeAsync(int eventId, string sponsorCompanyId, string? revokedByEmail, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var actives = await _db.SponsorApiKeys
            .Where(k => k.EventId == eventId
                        && k.SponsorCompanyId == sponsorCompanyId
                        && k.RevokedAt == null)
            .ToListAsync(ct);
        if (actives.Count == 0) return; // idempotent
        foreach (var k in actives) { k.RevokedAt = now; }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> ValidateAsync(int eventId, string sponsorCompanyId, string rawKey, CancellationToken ct)
    {
        var row = await GetCurrentAsync(eventId, sponsorCompanyId, ct);
        if (row is null) return false;
        var hash = Sha256Hex(rawKey);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(row.KeyHash));
    }

    // ---- helpers (identical scheme to the scaffold) ---------------------

    private static string RandomHex(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        var sb = new StringBuilder(byteCount * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string Sha256Hex(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
