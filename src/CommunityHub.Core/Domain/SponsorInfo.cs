namespace CommunityHub.Core.Domain;

/// <summary>
/// One sponsor company's self-service info: logos + descriptive text.
/// Scoped to (EventId, SponsorCompanyId) so all contacts of a company edit
/// the same row -- first one to save sets values; subsequent contacts edit
/// the same row. Replaces the email-based "send us your description" flow:
/// the data lives here, the hub auto-marks the matching ParticipantTask
/// rows Done when this is saved.
/// </summary>
public class SponsorInfo
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>WooCommerce / Company Manager company id.</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    // --- Logos (relative paths under wwwroot, e.g. uploads/sponsors/<co>/logo.eps) -
    public string? LogoVectorPath { get; set; }
    public string? LogoVectorFileName { get; set; }
    public string? LogoRasterPath { get; set; }
    public string? LogoRasterFileName { get; set; }

    // --- Descriptions (char limits enforced in the form + service) -----------
    /// <summary>Up to 1000 chars. Used for company profile page and Zoho.</summary>
    public string? CompanyDescription { get; set; }

    /// <summary>Up to 80 chars. One-liner shown on listings.</summary>
    public string? CompanyDescriptionShort { get; set; }

    /// <summary>Up to 600 chars. Social-media announcement intro (bullets fine).</summary>
    public string? SocialMediaIntro { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }
}
