using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Sponsors;

/// <summary>
/// SCAFFOLD implementation of <see cref="ISponsorApiKeyService"/>.
/// Holds keys in a process-local <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// keyed by (eventId, sponsorCompanyId). EVERYTHING IS LOST when the
/// App Service restarts -- which is fine for a scaffold but means
/// real sponsor keys are NOT to be issued from this implementation.
///
/// The persistent successor is a small class
/// <c>DbSponsorApiKeyService</c> that does the same operations against
/// <c>DbSet&lt;SponsorApiKey&gt;</c>. That's a single follow-up commit
/// once the EF migration has been written + reviewed + applied to the
/// dev / prod databases.
///
/// The hashing scheme + the validation surface match the persistent
/// version exactly, so swapping the DI registration is the only code
/// change needed when the persistent implementation lands.
/// </summary>
public sealed class InMemorySponsorApiKeyService : ISponsorApiKeyService
{
    private readonly ConcurrentDictionary<(int eventId, string companyId), SponsorApiKey> _byPair = new();
    private static int _nextId; // process-local; SCAFFOLD only.

    public Task<(string rawKey, SponsorApiKey row)> IssueAsync(
        int eventId, string sponsorCompanyId, string? issuedByEmail, string? label, CancellationToken ct)
    {
        // 32 random bytes -> 64 lowercase hex chars. Strong enough that
        // brute-forcing the hash via the validate endpoint is
        // infeasible inside a sponsor's relationship with the event.
        var raw = RandomHex(32);
        var hash = Sha256Hex(raw);

        var row = new SponsorApiKey
        {
            Id               = Interlocked.Increment(ref _nextId),
            EventId          = eventId,
            SponsorCompanyId = sponsorCompanyId,
            KeyHash          = hash,
            KeyPrefix        = raw[..8],
            Label            = label,
            IssuedAt         = DateTimeOffset.UtcNow,
            IssuedByEmail    = issuedByEmail,
        };

        // Revoke + replace any previous active key for this pair.
        _byPair[(eventId, sponsorCompanyId)] = row;
        return Task.FromResult((raw, row));
    }

    public Task<SponsorApiKey?> GetCurrentAsync(int eventId, string sponsorCompanyId, CancellationToken ct)
    {
        _byPair.TryGetValue((eventId, sponsorCompanyId), out var row);
        return Task.FromResult(row is { RevokedAt: null } ? row : null);
    }

    public Task RevokeAsync(int eventId, string sponsorCompanyId, string? revokedByEmail, CancellationToken ct)
    {
        if (_byPair.TryGetValue((eventId, sponsorCompanyId), out var row) && row.RevokedAt is null)
        {
            row.RevokedAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task<bool> ValidateAsync(int eventId, string sponsorCompanyId, string rawKey, CancellationToken ct)
    {
        if (!_byPair.TryGetValue((eventId, sponsorCompanyId), out var row)) return Task.FromResult(false);
        if (row.RevokedAt is not null) return Task.FromResult(false);
        var hash = Sha256Hex(rawKey);
        return Task.FromResult(CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(row.KeyHash)));
    }

    // ---- helpers ------------------------------------------------------

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
