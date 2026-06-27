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

    /// <summary>
    /// The active §57 session sync direction/stage for an edition. No row (or a row
    /// that predates §57) ⇒ the default stage 1 (<see cref="SessionSyncDirection.SessionizeToCeh"/>),
    /// so §38e stays inert until an organizer advances to stage 3.
    /// </summary>
    public async Task<SessionSyncDirection> GetSyncDirectionAsync(
        int eventId, CancellationToken ct = default)
    {
        var stored = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => (SessionSyncDirection?)s.SyncDirection)
            .FirstOrDefaultAsync(ct);
        return stored ?? SessionSyncDirection.SessionizeToCeh;
    }

    /// <summary>
    /// Set the active §57 sync direction/stage for an edition (upsert). Leaves the
    /// active <see cref="SessionSourceSetting.Source"/> untouched (it has a NOT NULL
    /// default of the shipped source) so flipping the stage alone is safe. Returns the
    /// stored direction.
    /// </summary>
    public async Task<SessionSyncDirection> SetSyncDirectionAsync(
        int eventId, SessionSyncDirection direction, string? byEmail, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentException($"Unknown sync direction '{direction}'.", nameof(direction));

        var row = await _db.SessionSourceSettings
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);
        if (row is null)
        {
            // A fresh row needs a valid Source (NOT NULL). Seed it to the shipped default.
            row = new SessionSourceSetting { EventId = eventId, Source = SessionSourceKinds.Default };
            _db.SessionSourceSettings.Add(row);
        }
        row.SyncDirection = direction;
        row.UpdatedByEmail = byEmail;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return direction;
    }

    /// <summary>
    /// REQUIREMENTS §58 — the active SPEAKER sync direction/stage for an edition. No row
    /// (or a row that predates §58) ⇒ the default stage 1
    /// (<see cref="SessionSyncDirection.SessionizeToCeh"/>), so any future Zoho→CEH speaker
    /// change-detection stays inert until an organizer advances to stage 3. Independent of
    /// the session <see cref="GetSyncDirectionAsync"/>.
    /// </summary>
    public async Task<SessionSyncDirection> GetSpeakerSyncDirectionAsync(
        int eventId, CancellationToken ct = default)
    {
        var stored = await _db.SessionSourceSettings.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => (SessionSyncDirection?)s.SpeakerSyncDirection)
            .FirstOrDefaultAsync(ct);
        return stored ?? SessionSyncDirection.SessionizeToCeh;
    }

    /// <summary>
    /// REQUIREMENTS §58 — set the active SPEAKER sync direction/stage for an edition
    /// (upsert). Leaves the active <see cref="SessionSourceSetting.Source"/> and the session
    /// <see cref="SessionSourceSetting.SyncDirection"/> untouched, so flipping the speaker
    /// stage alone is safe. Returns the stored direction.
    /// </summary>
    public async Task<SessionSyncDirection> SetSpeakerSyncDirectionAsync(
        int eventId, SessionSyncDirection direction, string? byEmail, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentException($"Unknown sync direction '{direction}'.", nameof(direction));

        var row = await _db.SessionSourceSettings
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);
        if (row is null)
        {
            // A fresh row needs a valid Source (NOT NULL). Seed it to the shipped default.
            row = new SessionSourceSetting { EventId = eventId, Source = SessionSourceKinds.Default };
            _db.SessionSourceSettings.Add(row);
        }
        row.SpeakerSyncDirection = direction;
        row.UpdatedByEmail = byEmail;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return direction;
    }

    /// <summary>
    /// REQUIREMENTS §58 GATE helper — true only when the edition's SPEAKER sync direction is
    /// stage 3 (<see cref="SessionSyncDirection.ZohoToCeh"/>). A future Zoho→CEH speaker
    /// change-detection engine MUST consult this before running, exactly as the §38e session
    /// engine gates on the session direction. At the default stage 1 (and stage 2) this is
    /// false, so the (not-yet-built) speaker engine stays inert.
    /// </summary>
    public async Task<bool> IsSpeakerZohoToCehActiveAsync(int eventId, CancellationToken ct = default) =>
        await GetSpeakerSyncDirectionAsync(eventId, ct) == SessionSyncDirection.ZohoToCeh;
}
