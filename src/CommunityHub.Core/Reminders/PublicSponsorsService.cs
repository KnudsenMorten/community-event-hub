using CommunityHub.Core.Data;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// One sponsor on the PUBLIC sponsors page (<c>/Sponsors</c>). Read-only projection:
/// the resolved public company name, the (optional) raster logo path under wwwroot,
/// and the (optional) public website link. Sponsors are public, so there is no
/// publish gate — every sponsor company the hub knows is listed.
/// </summary>
public sealed record PublicSponsorRow(
    string CompanyId,
    string Name,
    string? LogoPath,
    string? WebsiteUrl)
{
    /// <summary>
    /// Up to two uppercase initials from the company name, for the monogram shown
    /// when there is no logo (the graceful logo fallback).
    /// </summary>
    public string Initials => PublicInitials.From(Name);
}

/// <summary>One tier group on the public sponsors page (e.g. "Platinum" + its sponsors).</summary>
public sealed record PublicSponsorTierGroup(
    BoothTier Tier,
    string DisplayName,
    IReadOnlyList<PublicSponsorRow> Sponsors);

/// <summary>The whole public sponsors page: the tier groups, in tier order.</summary>
public sealed record PublicSponsorsView(
    string EventDisplayName,
    IReadOnlyList<PublicSponsorTierGroup> Groups,
    int TotalCount);

/// <summary>
/// Builds the data for the PUBLIC, no-login sponsors page (REQUIREMENTS § 7).
/// Scoped to the currently <b>active</b> edition (same active-event resolution as
/// the public sessions / speakers overviews). Lists the edition's sponsor companies
/// <b>grouped by tier</b>, highest tier first, each with their logo + resolved public
/// company name + optional link.
///
/// The public company NAME always resolves through the shared
/// <see cref="SponsorCompanyName"/> fallback chain (public → … → "Company {id}") so
/// the name never drifts from the rest of the hub. The hub has no company entity, so
/// the display name is sourced from the per-company <c>SponsorUploadLocation.CompanyName</c>
/// captured at order-pull time, with the "Company {id}" fallback when none is known.
///
/// Read-only: it never writes.
/// </summary>
public sealed class PublicSponsorsService
{
    private readonly CommunityHubDbContext _db;

    public PublicSponsorsService(CommunityHubDbContext db) => _db = db;

    /// <summary>Display order: Platinum, Diamond, Gold, Feature, then "other" (None).</summary>
    private static int TierRank(BoothTier t) => t switch
    {
        BoothTier.Platinum => 0,
        BoothTier.Diamond => 1,
        BoothTier.Gold => 2,
        BoothTier.Feature => 3,
        _ => 4, // None / other supporters
    };

    /// <summary>Human-friendly tier heading for the public page.</summary>
    public static string TierDisplay(BoothTier t) => t switch
    {
        BoothTier.Platinum => "Platinum",
        BoothTier.Diamond => "Diamond",
        BoothTier.Gold => "Gold",
        BoothTier.Feature => "Feature",
        _ => "Other supporters",
    };

    /// <summary>
    /// Build the public sponsors view for the active edition. Returns <c>null</c>
    /// when there is no active event (the page then renders a friendly "no event"
    /// empty state). When an event is active but no sponsor is on file yet, returns
    /// a view with zero groups (the page shows a "sponsors coming soon" empty state).
    /// </summary>
    public async Task<PublicSponsorsView?> BuildAsync(CancellationToken ct = default)
    {
        var active = await _db.Events
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Id, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (active is null) return null;

        var eventId = active.Id;

        var sponsors = await _db.SponsorInfos
            .Where(s => s.EventId == eventId)
            .Select(s => new
            {
                s.SponsorCompanyId,
                s.Tier,
                s.LogoRasterPath,
                s.WebsiteUrl,
            })
            .ToListAsync(ct);

        // The hub has no company entity; the display name is captured per company on
        // SponsorUploadLocation. Pull the per-company names once and resolve through
        // the shared fallback chain so the name matches every other sponsor surface.
        var names = await _db.SponsorUploadLocations
            .Where(l => l.EventId == eventId)
            .Select(l => new { l.SponsorCompanyId, l.CompanyName })
            .ToListAsync(ct);
        var nameByCompany = names
            .Where(n => !string.IsNullOrWhiteSpace(n.CompanyName))
            .GroupBy(n => n.SponsorCompanyId)
            .ToDictionary(g => g.Key, g => g.First().CompanyName, StringComparer.OrdinalIgnoreCase);

        var rows = sponsors
            .Select(s =>
            {
                nameByCompany.TryGetValue(s.SponsorCompanyId, out var publicName);
                var name = SponsorCompanyName.Resolve(
                    publicName, legalName: null, billingName: null, companyId: s.SponsorCompanyId);
                return new
                {
                    s.Tier,
                    Row = new PublicSponsorRow(
                        s.SponsorCompanyId,
                        name,
                        NormalizeLogoPath(s.LogoRasterPath),
                        SafeUrl(s.WebsiteUrl)),
                };
            })
            .ToList();

        var groups = rows
            .GroupBy(r => r.Tier)
            .OrderBy(g => TierRank(g.Key))
            .Select(g => new PublicSponsorTierGroup(
                g.Key,
                TierDisplay(g.Key),
                g.Select(r => r.Row)
                 .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                 .ToList()))
            .ToList();

        return new PublicSponsorsView(active.DisplayName, groups, rows.Count);
    }

    /// <summary>
    /// Logo paths are stored relative to wwwroot (e.g. <c>uploads/sponsors/&lt;co&gt;/logo.png</c>);
    /// normalise to a root-relative URL the page can use directly. Vector formats
    /// (.eps/.svg-as-vector) the browser can't render as a raster are dropped so the
    /// page falls back to the monogram rather than a broken image.
    /// </summary>
    private static string? NormalizeLogoPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('\\', '/');
        var ext = Path.GetExtension(p).ToLowerInvariant();
        if (ext is ".eps" or ".ai" or ".pdf") return null; // not browser-renderable
        if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith('/'))
        {
            return p;
        }
        return "/" + p;
    }

    /// <summary>Only ever surface an absolute http(s) link; anything else is dropped.</summary>
    private static string? SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        return (Uri.TryCreate(u, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
            ? u
            : null;
    }
}
