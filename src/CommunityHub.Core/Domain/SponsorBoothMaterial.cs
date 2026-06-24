namespace CommunityHub.Core.Domain;

/// <summary>Kind of booth material a sponsor maintains.</summary>
public enum BoothMaterialKind
{
    /// <summary>A video URL (YouTube/Vimeo/etc).</summary>
    Video = 0,
    /// <summary>An uploaded collateral file (brochure etc.) stored in SharePoint.</summary>
    Collateral = 1,
}

/// <summary>
/// One exhibitor "booth material" — either a video URL or an uploaded collateral
/// file — maintained in CEH (add/delete) for an exhibitor company. Both are STORED
/// in SQL (operator 2026-06-24). Scoped to (EventId, SponsorCompanyId); exhibitor
/// companies only. Up to 6 of each kind.
/// </summary>
public class SponsorBoothMaterial
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>WooCommerce / Company Manager company id (same key as SponsorInfo).</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    public BoothMaterialKind Kind { get; set; }

    /// <summary>Video URL (Video) or the SharePoint web URL of the uploaded file (Collateral).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Original file name for Collateral; null for Video.</summary>
    public string? FileName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
