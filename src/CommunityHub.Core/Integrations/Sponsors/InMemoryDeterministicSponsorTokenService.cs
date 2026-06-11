using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace CommunityHub.Core.Integrations.Sponsors;

/// <summary>
/// In-memory implementation of <see cref="IDeterministicSponsorTokenService"/>.
/// Token version map evaporates on App Service restart -- any sponsor whose
/// version was bumped reverts to 1. That's a known SCAFFOLD limitation:
/// when the <c>SponsorTokenVersion</c> EF migration lands, swap the DI
/// registration to <c>DbDeterministicSponsorTokenService</c> (same
/// contract) and the bumps become durable.
///
/// The global secret -- the one shared bytes that turn the per-sponsor
/// inputs into an unguessable token -- is read from configuration at
/// <c>SponsorLeads:GlobalSecret</c>. This binds to Key Vault secret
/// <c>sponsor-leads-global-secret</c> in production and dev. A missing
/// or whitespace value FAILS LOUD (constructor throws) instead of
/// silently producing a guessable token from "" + sponsor id.
/// </summary>
public sealed class InMemoryDeterministicSponsorTokenService : IDeterministicSponsorTokenService
{
    private readonly string _globalSecret;
    private readonly ConcurrentDictionary<(int EventId, string SponsorCompanyId), int> _versions = new();

    public InMemoryDeterministicSponsorTokenService(IConfiguration cfg)
    {
        // Section name: SponsorLeads:GlobalSecret. The web host's Key
        // Vault binding maps the kv secret 'sponsor-leads-global-secret'
        // to this configuration path automatically.
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

    public Task<int> GetVersionAsync(int eventId, string sponsorCompanyId, CancellationToken ct)
        => Task.FromResult(_versions.TryGetValue((eventId, sponsorCompanyId), out var v) ? v : 1);

    public async Task<int> BumpVersionAsync(int eventId, string sponsorCompanyId, string? bumpedByEmail, CancellationToken ct)
    {
        var next = _versions.AddOrUpdate((eventId, sponsorCompanyId), 2, (_, prev) => prev + 1);
        // Audit-trail hook -- empty for the in-memory impl; the durable
        // implementation writes (eventId, sponsorCompanyId, newVersion,
        // bumpedByEmail, bumpedAt) to a SponsorTokenVersionAudit table.
        _ = bumpedByEmail;
        return await Task.FromResult(next);
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

    // -----------------------------------------------------------------

    private static string Derive(int eventId, string sponsorCompanyId, int version, string globalSecret)
    {
        // Concatenate using a delimiter that can't appear in a guid /
        // company-id so different (eventId, cid, version) combinations
        // can't collide via canonical-form aliasing.
        var payload = string.Join("\u001E", new[]
        {
            eventId.ToString(CultureInfo.InvariantCulture),
            sponsorCompanyId.ToLowerInvariant(),
            version.ToString(CultureInfo.InvariantCulture),
            globalSecret
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        // 32 lowercase hex chars (128 bits) -- comfortably above brute-force.
        var sb = new StringBuilder(32);
        for (var i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}
