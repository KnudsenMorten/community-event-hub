using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;

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

    /// <summary>
    /// The sponsorship tier this company holds (the booth tier — Gold / Diamond /
    /// Platinum / Feature, or <see cref="BoothTier.None"/> when unknown). Drives the
    /// grouping on the PUBLIC sponsors page (<c>/Sponsors</c>), where sponsors are
    /// listed by tier. Sponsors are public, so there is no publish gate — a company
    /// is shown once it has a tier (or as an "other supporters" group when
    /// <see cref="BoothTier.None"/>). Set from the product classification when a
    /// sponsor's booth order is processed; an organizer may correct it.
    /// </summary>
    public BoothTier Tier { get; set; } = BoothTier.None;

    /// <summary>
    /// Optional public website URL shown as the sponsor's link on the public
    /// sponsors page. Hub-collected (a sponsor contact / organizer can set it);
    /// null/blank renders no link. Format is validated in the editing UI; the
    /// public page only ever renders an absolute http(s) URL.
    /// </summary>
    public string? WebsiteUrl { get; set; }

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

    // --- Release ring (company default for its contacts, REQUIREMENTS §23) ----
    /// <summary>
    /// This sponsor company's DEFAULT release ring — the fallback access level for
    /// every contact of this company that has no contact-level ring of its own.
    /// A contact's own <see cref="Participant.Ring"/> SUPERSEDES this; the
    /// effective ring of a sponsor contact is
    /// <c>contact.Ring ?? company.Ring ?? Broad</c> (link via
    /// <see cref="Participant.SponsorCompanyId"/> == <see cref="SponsorCompanyId"/>).
    ///
    /// Defaults to <see cref="Ring.Broad"/> (general availability) so an
    /// unassigned company behaves exactly as today (its contacts see only
    /// fully-released features unless given an earlier ring).
    /// </summary>
    public Ring Ring { get; set; } = Rings.Default;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }
}
