using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// Manages the per-persona-group SharePoint asset locations (REQUIREMENTS §18) —
/// the organizer admin settings place where each group (volunteers / speakers /
/// media / organizers) is told WHERE its bio details / files are stored. Real
/// SharePoint links are operator-entered; this only persists/reads the pointers.
/// </summary>
public sealed class AssetLocationService
{
    private readonly CommunityHubDbContext _db;

    public AssetLocationService(CommunityHubDbContext db) => _db = db;

    /// <summary>All configured locations for an edition, one (at most) per persona group.</summary>
    public async Task<IReadOnlyList<GraphicsAssetLocation>> ListAsync(
        int eventId, CancellationToken ct = default) =>
        await _db.GraphicsAssetLocations
            .Where(l => l.EventId == eventId)
            .OrderBy(l => l.PersonaGroup)
            .ToListAsync(ct);

    /// <summary>The configured location for one persona group, or null if not set.</summary>
    public Task<GraphicsAssetLocation?> GetAsync(
        int eventId, AssetPersonaGroup group, CancellationToken ct = default) =>
        _db.GraphicsAssetLocations
            .FirstOrDefaultAsync(l => l.EventId == eventId && l.PersonaGroup == group, ct);

    /// <summary>
    /// UPSERT a persona group's location (one row per (edition, group)). Stamps the
    /// editor + timestamp. Returns the saved row.
    /// </summary>
    public async Task<GraphicsAssetLocation> UpsertAsync(
        int eventId,
        AssetPersonaGroup group,
        string? siteUrl,
        string? driveName,
        string? rootFolderPath,
        string? browseUrl,
        string? notes,
        string? editorEmail,
        CancellationToken ct = default)
    {
        var row = await GetAsync(eventId, group, ct);
        var now = DateTimeOffset.UtcNow;

        if (row is null)
        {
            row = new GraphicsAssetLocation
            {
                EventId = eventId,
                PersonaGroup = group,
                CreatedAt = now,
            };
            _db.GraphicsAssetLocations.Add(row);
        }

        row.SiteUrl = Trim(siteUrl);
        row.DriveName = Trim(driveName);
        row.RootFolderPath = Trim(rootFolderPath);
        row.BrowseUrl = Trim(browseUrl);
        row.Notes = Trim(notes);
        row.LastUpdatedByEmail = editorEmail;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return row;
    }

    private static string? Trim(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
