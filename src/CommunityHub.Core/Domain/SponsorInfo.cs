using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;

namespace CommunityHub.Core.Domain;

/// <summary>
/// The sponsorship PACKAGE a company holds, ordered cheapest → richest.
/// Distinct from <see cref="Integrations.BoothTier"/> (the physical booth-wall
/// spec): the package is the commercial level that decides whether the company
/// gets a booth at all. Silver is digital-only (no booth); Gold and above are
/// exhibitor packages that include a booth.
/// </summary>
public enum SponsorPackage
{
    /// <summary>Digital / no booth.</summary>
    Silver = 0,

    /// <summary>Booth / exhibitor.</summary>
    Gold = 1,

    /// <summary>Booth / exhibitor.</summary>
    Diamond = 2,

    /// <summary>Booth / exhibitor.</summary>
    Platinum = 3,
}

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
    /// Zoho Backstage SPONSOR id for this company (every paying company is a Zoho
    /// sponsor). Persisted so the Zoho sync targets this company by id instead of
    /// matching on company name (which can change). Null until mapped. See the
    /// one-time ID-fetch that matches by company name across CEH / Zoho / webshop.
    /// </summary>
    public string? ZohoSponsorId { get; set; }

    /// <summary>
    /// Zoho Backstage EXHIBITOR id for this company — present only when the company
    /// bought booth products (so it appears as an exhibitor as well as a sponsor).
    /// Null for sponsor-only companies. A company can therefore carry TWO Zoho ids.
    /// </summary>
    public string? ZohoExhibitorId { get; set; }

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
    /// The commercial sponsorship package this company bought
    /// (Silver/Gold/Diamond/Platinum). Defaults to
    /// <see cref="SponsorPackage.Silver"/> (digital, no booth). Set from the
    /// purchased product name at sync time (see
    /// <see cref="Integrations.SponsorPackageMapper"/>); an organizer may correct
    /// it. Drives <see cref="HasBooth"/> and the sponsor-hat order entitlements.
    /// </summary>
    public SponsorPackage SponsorPackage { get; set; } = SponsorPackage.Silver;

    /// <summary>
    /// True when this company's package includes a booth/exhibitor presence
    /// (Gold and above). Silver is digital-only. Computed from
    /// <see cref="SponsorPackage"/>; not persisted.
    /// </summary>
    public bool HasBooth => SponsorPackage >= SponsorPackage.Gold;

    /// <summary>
    /// Optional public website URL shown as the sponsor's link on the public
    /// sponsors page. Hub-collected (a sponsor contact / organizer can set it);
    /// null/blank renders no link. Format is validated in the editing UI; the
    /// public page only ever renders an absolute http(s) URL.
    /// </summary>
    public string? WebsiteUrl { get; set; }

    /// <summary>
    /// Company LinkedIn page URL (full https URL, e.g.
    /// https://www.linkedin.com/company/2linkit). Hub-collected on the Company
    /// Details page; synced to Zoho Backstage exhibitor <c>company_social_pages</c>.
    /// </summary>
    public string? LinkedInUrl { get; set; }

    /// <summary>
    /// Company Twitter/X page URL (full https URL). Hub-collected on the Company
    /// Details page; synced to Zoho Backstage exhibitor <c>company_social_pages</c>.
    /// </summary>
    public string? TwitterUrl { get; set; }

    // --- Event Coordinator (the sponsor's primary contact) -------------------
    // Synced to the Zoho sponsor/exhibitor `contact` object (first/last/email +
    // phone on the exhibitor's mobile_no). Seeded by a one-time migration from the
    // webshop default event coordinator; thereafter CEH owns it (editable on
    // Company Details).
    public string? EventCoordinatorFirstName { get; set; }
    public string? EventCoordinatorLastName { get; set; }
    public string? EventCoordinatorCompanyName { get; set; }
    public string? EventCoordinatorEmail { get; set; }
    public string? EventCoordinatorPhone { get; set; }

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
