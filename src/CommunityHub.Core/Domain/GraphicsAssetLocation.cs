namespace CommunityHub.Core.Domain;

/// <summary>
/// The persona groups an asset-location can be configured for (REQUIREMENTS §18).
/// Each group's bio details / files live at a different SharePoint link, set by an
/// organizer in the admin settings page.
/// </summary>
public enum AssetPersonaGroup
{
    Volunteers = 0,
    Speakers = 1,
    Media = 2,
    Organizers = 3,
}

/// <summary>
/// One configured SharePoint location, per persona group, that tells the hub WHERE
/// that group's bio details / files / graphics are stored (REQUIREMENTS §18).
/// Scoped to an <see cref="Event"/> edition. Organizers manage these in the admin
/// settings place; the real SharePoint links are operator-entered (committed
/// config carries placeholders only).
///
/// Only the per-edition site / drive / folder POINTERS live here — never SPN
/// credentials. The Graph SPN creds are deployment-scoped (Key Vault) and live in
/// <see cref="Integrations.SharePointUploadOptions"/> / the file-store options.
/// </summary>
public class GraphicsAssetLocation
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Which persona group this location is for.</summary>
    public AssetPersonaGroup PersonaGroup { get; set; }

    /// <summary>
    /// The SharePoint site URL where this group's files live (operator-entered;
    /// placeholder in committed config).
    /// </summary>
    public string? SiteUrl { get; set; }

    /// <summary>The document library / drive name (blank = the site's default drive).</summary>
    public string? DriveName { get; set; }

    /// <summary>
    /// The root folder path under the drive for this group (e.g.
    /// <c>/Graphics/Speakers</c>). Files / graphics for the group are stored under
    /// it.
    /// </summary>
    public string? RootFolderPath { get; set; }

    /// <summary>
    /// A human-facing link an organizer can click to open the group's folder /
    /// bio-details location in SharePoint (the "where bio details / files are
    /// stored" link). Operator-entered.
    /// </summary>
    public string? BrowseUrl { get; set; }

    /// <summary>Free-text note for organizers (what goes here, who maintains it).</summary>
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }
}
