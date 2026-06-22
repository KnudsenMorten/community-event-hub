using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CommunityHub.Core.Email;

/// <summary>
/// Reads + upserts the per-edition EMAIL-TEMPLATE OVERRIDES (REQUIREMENTS §25h). An
/// <see cref="EmailTemplateOverride"/> row holds the FULL edited template text for one
/// template key; the renderer uses it INSTEAD of the shipped on-disk template at send +
/// preview time. No row ⇒ shipped default. Reset-to-default = <see cref="DeleteAsync"/>.
///
/// Cached per (edition, templateKey) in <see cref="IMemoryCache"/> so the send path hits
/// the DB once per change, not per email; upsert/delete invalidate. Mirrors
/// <see cref="Config.ConfigOverrideStore"/>.
/// </summary>
public sealed class EmailTemplateOverrideStore
{
    private readonly CommunityHubDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public EmailTemplateOverrideStore(
        CommunityHubDbContext db, IMemoryCache cache, TimeProvider clock)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
    }

    private static string CacheKey(int eventId, string templateKey) =>
        $"emailtploverride::{eventId}::{templateKey}";

    /// <summary>
    /// The override text for an (edition, templateKey), or null when none is persisted
    /// (⇒ shipped default applies). The absence of a row is cached too. A blank override
    /// is treated as "no override".
    /// </summary>
    public async Task<string?> GetOverrideTextAsync(
        int eventId, string templateKey, CancellationToken ct = default)
    {
        var key = CacheKey(eventId, templateKey);
        if (_cache.TryGetValue(key, out string? cached)) return cached;

        var row = await _db.EmailTemplateOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.TemplateKey == templateKey, ct);

        var text = row?.OverrideText;
        if (string.IsNullOrWhiteSpace(text)) text = null;

        _cache.Set(key, text, CacheTtl);
        return text;
    }

    /// <summary>The full persisted row (incl. audit fields) for the editor, or null. Uncached.</summary>
    public Task<EmailTemplateOverride?> GetAsync(
        int eventId, string templateKey, CancellationToken ct = default) =>
        _db.EmailTemplateOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.TemplateKey == templateKey, ct);

    /// <summary>All overrides for an edition (so the editor can show which are overridden in one query).</summary>
    public async Task<IReadOnlyList<EmailTemplateOverride>> GetAllAsync(
        int eventId, CancellationToken ct = default) =>
        await _db.EmailTemplateOverrides
            .AsNoTracking()
            .Where(o => o.EventId == eventId)
            .ToListAsync(ct);

    /// <summary>Create-or-update the override text for an (edition, templateKey) + invalidate cache.</summary>
    public async Task UpsertAsync(
        int eventId, string templateKey, string overrideText, string? byEmail,
        CancellationToken ct = default)
    {
        var row = await _db.EmailTemplateOverrides
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.TemplateKey == templateKey, ct);
        if (row is null)
        {
            row = new EmailTemplateOverride { EventId = eventId, TemplateKey = templateKey };
            _db.EmailTemplateOverrides.Add(row);
        }

        row.OverrideText = overrideText ?? string.Empty;
        row.UpdatedAt = _clock.GetUtcNow();
        row.UpdatedByEmail = string.IsNullOrWhiteSpace(byEmail) ? null : byEmail.Trim();

        await _db.SaveChangesAsync(ct);
        _cache.Remove(CacheKey(eventId, templateKey));
    }

    /// <summary>Reset-to-default: remove the override (if any) + invalidate cache.</summary>
    public async Task DeleteAsync(
        int eventId, string templateKey, CancellationToken ct = default)
    {
        var row = await _db.EmailTemplateOverrides
            .FirstOrDefaultAsync(o => o.EventId == eventId && o.TemplateKey == templateKey, ct);
        if (row is not null)
        {
            _db.EmailTemplateOverrides.Remove(row);
            await _db.SaveChangesAsync(ct);
        }
        _cache.Remove(CacheKey(eventId, templateKey));
    }
}
