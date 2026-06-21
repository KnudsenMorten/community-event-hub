using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// Reads + persists the per-edition active session source (REQUIREMENTS §6). No
/// row ⇒ the shipped default (<see cref="SessionSourceKinds.Default"/>). An unknown
/// stored value also falls back to the default, so a bad write can never wedge the
/// import.
/// </summary>
public sealed class SessionSourceSettingsService
{
    private readonly CommunityHubDbContext _db;
    public SessionSourceSettingsService(CommunityHubDbContext db) => _db = db;

    /// <summary>The active source key for an edition (default when unset/unknown).</summary>
    public async Task<string> GetActiveKeyAsync(int eventId, CancellationToken ct = default)
    {
        var stored = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => s.Source)
            .FirstOrDefaultAsync(ct);
        return SessionSourceKinds.IsKnown(stored) ? stored! : SessionSourceKinds.Default;
    }

    /// <summary>
    /// Set the active source for an edition (upsert). Rejects an unknown key. Returns
    /// the stored key.
    /// </summary>
    public async Task<string> SetAsync(
        int eventId, string key, string? byEmail, CancellationToken ct = default)
    {
        if (!SessionSourceKinds.IsKnown(key))
            throw new ArgumentException($"Unknown session source '{key}'.", nameof(key));

        var row = await _db.SessionSourceSettings
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);
        if (row is null)
        {
            row = new SessionSourceSetting { EventId = eventId };
            _db.SessionSourceSettings.Add(row);
        }
        row.Source = key;
        row.UpdatedByEmail = byEmail;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return key;
    }
}
