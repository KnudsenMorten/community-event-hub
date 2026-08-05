using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §769 — reads and writes the operator's document-library path / file-name edits, and records the
/// change history the work order asks for.
/// </summary>
/// <remarks>
/// <para>Scoped (it holds a DbContext). The RESOLVER does not use this directly — it reads
/// <see cref="DocLibraryOverrideCache"/>, a singleton snapshot — because resolving a path is a
/// synchronous call on hot paths that must not touch the database.</para>
///
/// <para>🔒 <b>Every write goes through here, so every write is historied.</b> The history is the
/// point: a folder that moved and a feature that went quiet three days later are the same event, and
/// without "who changed this, when, from what" nobody connects them. §768 spent a week reconstructing
/// exactly that from Azure settings which keep no such trail.</para>
/// </remarks>
public sealed class DocLibraryOverrideStore
{
    private readonly CommunityHubDbContext _db;
    private readonly DocLibraryOverrideCache _cache;
    private readonly TimeProvider _clock;

    public DocLibraryOverrideStore(
        CommunityHubDbContext db, DocLibraryOverrideCache cache, TimeProvider? clock = null)
    {
        _db = db;
        _cache = cache;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Every override, newest edit first.</summary>
    public Task<List<DocLibrarySettingOverride>> AllAsync(CancellationToken ct = default) =>
        _db.DocLibrarySettingOverrides.AsNoTracking()
            .OrderBy(x => x.Kind).ThenBy(x => x.Key)
            .ToListAsync(ct);

    /// <summary>The change history, newest first.</summary>
    public Task<List<DocLibrarySettingChange>> HistoryAsync(int take = 100, CancellationToken ct = default) =>
        _db.DocLibrarySettingChanges.AsNoTracking()
            .OrderByDescending(x => x.ChangedAt).ThenByDescending(x => x.Id)
            .Take(take)
            .ToListAsync(ct);

    /// <summary>
    /// Set one value. A blank <paramref name="value"/> RESTORES THE DEFAULT (deletes the row) rather
    /// than storing an empty string — an empty path resolves to the drive root, which is the most
    /// dangerous value in the system.
    /// </summary>
    /// <returns>True when something actually changed; false when the value was already in force.</returns>
    public async Task<bool> SetAsync(
        DocLibrarySettingKind kind, string key, string? value, string? byEmail,
        CancellationToken ct = default)
    {
        var trimmed = (value ?? string.Empty).Trim();
        var row = await _db.DocLibrarySettingOverrides
            .FirstOrDefaultAsync(x => x.Kind == kind && x.Key == key, ct);

        var now = _clock.GetUtcNow();
        var old = row?.Value;

        if (trimmed.Length == 0)
        {
            if (row is null) return false;                       // already on the default
            _db.DocLibrarySettingOverrides.Remove(row);
            AddHistory(kind, key, old, newValue: null, now, byEmail);
            await _db.SaveChangesAsync(ct);
            _cache.Invalidate();
            return true;
        }

        if (row is not null && string.Equals(row.Value, trimmed, StringComparison.Ordinal))
            return false;                                        // a save that changes nothing

        if (row is null)
        {
            _db.DocLibrarySettingOverrides.Add(new DocLibrarySettingOverride
            {
                Kind = kind, Key = key, Value = trimmed, UpdatedAt = now, UpdatedByEmail = byEmail,
            });
        }
        else
        {
            row.Value = trimmed;
            row.UpdatedAt = now;
            row.UpdatedByEmail = byEmail;
        }

        AddHistory(kind, key, old, trimmed, now, byEmail);
        await _db.SaveChangesAsync(ct);
        _cache.Invalidate();
        return true;
    }

    private void AddHistory(
        DocLibrarySettingKind kind, string key, string? oldValue, string? newValue,
        DateTimeOffset now, string? byEmail)
    {
        _db.DocLibrarySettingChanges.Add(new DocLibrarySettingChange
        {
            Kind = kind,
            Key = key,
            OldValue = oldValue,
            NewValue = newValue,
            ChangedAt = now,
            ChangedByEmail = byEmail,
        });
    }
}
