using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// §1360 — a sponsor company's website, LinkedIn and X: ONE field each, on the CEH company page
/// (<c>ShopCompany.Website / LinkedInUrl / TwitterUrl</c>). Operator 2026-10-19: *"why do we have double ?"* → *"one field,
/// truesec.dk"*.
/// </summary>
/// <remarks>
/// <para>🔒 Every reader goes through <see cref="AllAsync"/> / <see cref="ForAsync"/> — the public sponsor page, the AI helper,
/// SoMe, the LinkedIn organisation lookup, onboarding, the Zoho sync and provisioning, the sponsor's own forms and the links
/// reminder. The sponsor record's copy (<c>SponsorInfo.WebsiteUrl / LinkedInUrl / TwitterUrl</c>) is NOT read any more: it
/// is still written alongside (a legacy mirror) and only consulted for a company that has no company-page row at all.</para>
/// <para>Why: the two copies were kept equal only when someone saved through one of two forms, so a sponsor record created
/// after its company page (Egiss), a one-sided write (Truesec: .com vs .dk) or a blank on one side (RedHat) left the public
/// page, Zoho and SoMe showing something other than the company page — silently.</para>
/// </remarks>
public static class CompanyLinks
{
    public sealed record Links(string? Website, string? LinkedIn, string? Twitter)
    {
        public static readonly Links None = new(null, null, null);
    }

    /// <summary>Every company of the edition → its links (the company page; the sponsor record only where no page exists).</summary>
    public static async Task<IReadOnlyDictionary<string, Links>> AllAsync(CommunityHubDbContext db, int eventId, CancellationToken ct = default)
    {
        var pages = await db.ShopCompanies.AsNoTracking()
            .Where(c => c.EventId == eventId)
            .Select(c => new { c.SponsorCompanyId, c.Website, c.LinkedInUrl, c.TwitterUrl })
            .ToListAsync(ct);
        var result = new Dictionary<string, Links>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pages)
            result.TryAdd(p.SponsorCompanyId, new Links(Clean(p.Website), Clean(p.LinkedInUrl), Clean(p.TwitterUrl)));

        var legacy = await db.SponsorInfos.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.SponsorCompanyId, s.WebsiteUrl, s.LinkedInUrl, s.TwitterUrl })
            .ToListAsync(ct);
        foreach (var s in legacy)
            result.TryAdd(s.SponsorCompanyId, new Links(Clean(s.WebsiteUrl), Clean(s.LinkedInUrl), Clean(s.TwitterUrl)));
        return result;
    }

    /// <summary>One company's links (same rule as <see cref="AllAsync"/>).</summary>
    public static async Task<Links> ForAsync(CommunityHubDbContext db, int eventId, string? companyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId)) return Links.None;
        var page = await db.ShopCompanies.AsNoTracking()
            .Where(c => c.EventId == eventId && c.SponsorCompanyId == companyId)
            .Select(c => new Links(c.Website, c.LinkedInUrl, c.TwitterUrl))
            .FirstOrDefaultAsync(ct);
        if (page is not null) return new Links(Clean(page.Website), Clean(page.LinkedIn), Clean(page.Twitter));
        var legacy = await db.SponsorInfos.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.EventId == eventId && s.SponsorCompanyId == companyId)
            .Select(s => new Links(s.WebsiteUrl, s.LinkedInUrl, s.TwitterUrl))
            .FirstOrDefaultAsync(ct);
        return legacy is null ? Links.None : new Links(Clean(legacy.Website), Clean(legacy.LinkedIn), Clean(legacy.Twitter));
    }

    /// <summary>A lookup over a preloaded map, for loops (Zoho sync/provisioning).</summary>
    public static Links Of(IReadOnlyDictionary<string, Links> all, string? companyId) =>
        companyId is not null && all.TryGetValue(companyId, out var l) ? l : Links.None;

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
