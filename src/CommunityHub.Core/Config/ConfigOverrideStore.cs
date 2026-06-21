using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CommunityHub.Core.Config;

/// <summary>
/// Reads + upserts the per-edition CONFIG OVERRIDES (HYBRID config model,
/// Phase 1). A <see cref="ConfigOverride"/> row holds a partial JSON FRAGMENT for
/// one <see cref="ConfigSection"/> that the loaders deep-merge on top of the
/// shipped JSON default at runtime.
///
/// Cached: the override JSON for an (edition, section) is cached in
/// <see cref="IMemoryCache"/> so the DB is hit once per change, not per config
/// read; <see cref="UpsertAsync"/> and <see cref="DeleteAsync"/> invalidate the
/// entry. The cache key namespaces by (edition, section) so editions never see
/// each other's overrides.
///
/// FAIL-SAFE: when no row exists <see cref="GetOverrideJsonAsync"/> returns null
/// (⇒ the loader uses the shipped default unchanged). The deep-merge itself is
/// fail-safe to the default on a bad fragment (see <see cref="JsonDeepMerge"/>).
///
/// SECRETS: never persist secret VALUES here — only non-secret settings and Key
/// Vault secret NAMES. The store does not read or write Key Vault.
/// </summary>
public sealed class ConfigOverrideStore
{
    private readonly CommunityHubDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;

    /// <summary>How long a resolved override (or its absence) is cached.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public ConfigOverrideStore(
        CommunityHubDbContext db, IMemoryCache cache, TimeProvider clock)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
    }

    private static string CacheKey(int eventId, ConfigSection section) =>
        $"configoverride::{eventId}::{section}";

    /// <summary>
    /// The override JSON fragment for an (edition, section), or null when none is
    /// persisted (⇒ shipped default applies). Cached; the absence of a row is
    /// cached too (as null) so a defaults-only edition doesn't re-query per read.
    /// </summary>
    public async Task<string?> GetOverrideJsonAsync(
        int eventId, ConfigSection section, CancellationToken ct = default)
    {
        var key = CacheKey(eventId, section);
        if (_cache.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        var row = await _db.ConfigOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.Section == section, ct);

        var json = row?.OverrideJson;
        // Treat a blank fragment as "no override" so callers get a clean null.
        if (string.IsNullOrWhiteSpace(json)) json = null;

        _cache.Set(key, json, CacheTtl);
        return json;
    }

    /// <summary>The full persisted row for an (edition, section), or null. Not cached
    /// (used by the editor to show audit fields); reads bypass the JSON cache.</summary>
    public Task<ConfigOverride?> GetAsync(
        int eventId, ConfigSection section, CancellationToken ct = default) =>
        _db.ConfigOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.Section == section, ct);

    /// <summary>
    /// Create-or-update the override fragment for an (edition, section) and
    /// invalidate the cache. A blank fragment is allowed (it is stored as-is and
    /// treated as "no override" by readers); pass an empty string or call
    /// <see cref="DeleteAsync"/> to clear. This method does NOT validate the JSON
    /// shape — the deep-merge is fail-safe to the default on a bad fragment — but
    /// the editor (Phase 2) should validate before saving.
    /// </summary>
    public async Task UpsertAsync(
        int eventId, ConfigSection section, string overrideJson, string? byEmail,
        CancellationToken ct = default)
    {
        var row = await _db.ConfigOverrides
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.Section == section, ct);
        if (row is null)
        {
            row = new ConfigOverride { EventId = eventId, Section = section };
            _db.ConfigOverrides.Add(row);
        }

        row.OverrideJson = overrideJson ?? string.Empty;
        row.UpdatedAt = _clock.GetUtcNow();
        row.UpdatedByEmail = string.IsNullOrWhiteSpace(byEmail) ? null : byEmail.Trim();

        await _db.SaveChangesAsync(ct);
        _cache.Remove(CacheKey(eventId, section));
    }

    /// <summary>
    /// Remove the override for an (edition, section), if any, and invalidate the
    /// cache. After this the effective config is byte-for-byte the shipped
    /// default. No-op when no row exists.
    /// </summary>
    public async Task DeleteAsync(
        int eventId, ConfigSection section, CancellationToken ct = default)
    {
        var row = await _db.ConfigOverrides
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.Section == section, ct);
        if (row is not null)
        {
            _db.ConfigOverrides.Remove(row);
            await _db.SaveChangesAsync(ct);
        }
        _cache.Remove(CacheKey(eventId, section));
    }
}
