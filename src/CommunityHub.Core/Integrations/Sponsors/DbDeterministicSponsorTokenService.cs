using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CommunityHub.Core.Integrations.Sponsors;

/// <summary>
/// Durable implementation of <see cref="IDeterministicSponsorTokenService"/>
/// over DbSet&lt;SponsorTokenVersion&gt; — the successor the in-memory
/// scaffold's comment promised. Version bumps (= token revocations) now
/// survive App Service restarts and slot swaps. Derivation scheme is
/// IDENTICAL to the scaffold, so tokens already handed to sponsors at
/// version 1 keep working after the swap-over.
/// </summary>
public sealed class DbDeterministicSponsorTokenService : IDeterministicSponsorTokenService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly string _globalSecret;

    public DbDeterministicSponsorTokenService(
        CommunityHubDbContext db, TimeProvider clock, IConfiguration cfg)
    {
        _db = db;
        _clock = clock;
        var raw = cfg["SponsorLeads:GlobalSecret"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                "SponsorLeads:GlobalSecret is not configured. Create the Key Vault " +
                "secret 'sponsor-leads-global-secret' (>= 32 random hex chars) and " +
                "ensure the App Service's managed identity can read it before deploying.");
        }
        _globalSecret = raw.Trim();
    }

    public async Task<int> GetVersionAsync(int eventId, string sponsorCompanyId, CancellationToken ct)
    {
        var row = await _db.SponsorTokenVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.EventId == eventId
                                      && v.SponsorCompanyId == sponsorCompanyId, ct);
        return row?.Version ?? 1;
    }

    public async Task<int> BumpVersionAsync(int eventId, string sponsorCompanyId, string? bumpedByEmail, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var row = await _db.SponsorTokenVersions
            .FirstOrDefaultAsync(v => v.EventId == eventId
                                      && v.SponsorCompanyId == sponsorCompanyId, ct);
        if (row is null)
        {
            row = new SponsorTokenVersion
            {
                EventId = eventId,
                SponsorCompanyId = sponsorCompanyId,
                Version = 2, // absent row meant version 1; first bump = 2
            };
            _db.SponsorTokenVersions.Add(row);
        }
        else
        {
            row.Version += 1;
        }
        row.BumpedAt = now;
        row.BumpedByEmail = bumpedByEmail;
        await _db.SaveChangesAsync(ct);
        return row.Version;
    }

    public async Task<string> DeriveAsync(int eventId, string sponsorCompanyId, CancellationToken ct)
    {
        var version = await GetVersionAsync(eventId, sponsorCompanyId, ct);
        return Derive(eventId, sponsorCompanyId, version, _globalSecret);
    }

    public async Task<bool> ValidateAsync(int eventId, string sponsorCompanyId, string rawToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return false;
        var current = await DeriveAsync(eventId, sponsorCompanyId, ct);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(current.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(rawToken.Trim().ToLowerInvariant()));
    }

    // ---- identical derivation to the scaffold (token continuity) -------

    private static string Derive(int eventId, string sponsorCompanyId, int version, string globalSecret)
    {
        var payload = string.Join("\u001E", new[]
        {
            eventId.ToString(CultureInfo.InvariantCulture),
            sponsorCompanyId.ToLowerInvariant(),
            version.ToString(CultureInfo.InvariantCulture),
            globalSecret
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var sb = new StringBuilder(32);
        for (var i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}
