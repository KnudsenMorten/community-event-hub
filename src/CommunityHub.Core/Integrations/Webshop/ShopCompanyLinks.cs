using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// 🔴 §1312 — a sponsor's website, LinkedIn and X are ONE field each, owned by CEH (operator 2026-09-30: "it must be the same
/// field" · "ceh owns the field" · "old webshop is not owner of this anymore, we changed that sunday"). They live on two rows
/// — the sponsor record (read by Zoho, the event site, SoMe) and the shop company (the organizer's company page) — so every
/// save writes BOTH. Whoever saves last, sponsor or organizer, wins.
/// </summary>
public static class ShopCompanyLinks
{
    /// <summary>The sponsor record's links → the shop company (after the sponsor saved them).</summary>
    public static async Task MirrorToShopAsync(CommunityHubDbContext db, int eventId, string companyId, SponsorInfo info, CancellationToken ct = default)
    {
        var shop = await db.ShopCompanies.FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct);
        if (shop is null) return;
        shop.Website = info.WebsiteUrl;
        shop.LinkedInUrl = info.LinkedInUrl;
        shop.TwitterUrl = info.TwitterUrl;
    }

    /// <summary>Max length of the public name — the shop and sponsor-record columns are both 200.</summary>
    public const int PublicNameMax = 200;

    /// <summary>
    /// §1344 — the sponsor edits their PUBLIC name (event site, app, SoMe, Zoho, uploads) in Get Started and on Company
    /// Details (operator 2026-10-14: "i am missing the ability for a company to change their public name in the get
    /// started"). Same two rows as the organizer's edit (<c>ShopCompanyDirectoryService.UpdateCehDataAsync</c>): the sponsor
    /// record (read first by <c>SponsorCompanyNameService.LocalNameAsync</c>) and the shop company. 🔒 A blank never clears
    /// it — a company always has a public name — and the legal name (contract, invoice) is never touched.
    /// </summary>
    /// <returns>true when the name changed.</returns>
    public static async Task<bool> ApplyPublicNameAsync(CommunityHubDbContext db, int eventId, string companyId, SponsorInfo info,
        string? publicName, CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(publicName) ? null : publicName.Trim();
        if (name is null) return false;
        var changed = !string.Equals(info.CompanyName, name, StringComparison.Ordinal);
        info.CompanyName = name;
        var shop = await db.ShopCompanies.FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct);
        if (shop is not null && !string.Equals(shop.PublicName, name, StringComparison.Ordinal))
        {
            shop.PublicName = name;
            shop.UpdatedAt = DateTimeOffset.UtcNow;   // as the organizer's table save (SetPublicNameAsync) stamps it
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// §1344 — the public name the sponsor SEES: the organizer's field (<c>ShopCompany.PublicName</c>, edited on the
    /// organizer's company page) first, so sponsor and organizer always look at one value; else CEH's local name.
    /// </summary>
    public static async Task<string?> PublicNameAsync(CommunityHubDbContext db, int eventId, string companyId, CancellationToken ct = default)
    {
        var shopName = await db.ShopCompanies.AsNoTracking()
            .Where(c => c.EventId == eventId && c.SponsorCompanyId == companyId)
            .Select(c => c.PublicName).FirstOrDefaultAsync(ct);
        return !string.IsNullOrWhiteSpace(shopName)
            ? shopName.Trim()
            : await SponsorCompanyNameService.LocalNameAsync(db, companyId, ct);
    }

    /// <summary>Outcome of <see cref="SyncFromCompanyPageAsync"/>: copies rewritten, and blank page fields filled once from a copy.</summary>
    public sealed record SyncResult(int CopiesRewritten, int PageFieldsAbsorbed);

    /// <summary>
    /// 🔒 §1360 — the company page (<c>ShopCompany</c>) holds ONE public name, website, LinkedIn and X per company; the sponsor
    /// record's <c>CompanyName / WebsiteUrl / LinkedInUrl / TwitterUrl</c> are a COPY, rewritten from it on every sponsor-order
    /// pull. Operator 2026-10-19: *"why do we have double ?"* → *"one field"* · *"dont forget the public name"*. The copies had
    /// drifted (Egiss and RedHat blank, Truesec .com vs .dk) because the mirrors run only on a save.
    /// </summary>
    /// <remarks>
    /// Per field: the page's value wins and is copied down. The ONE exception, so nothing is lost: a BLANK page field takes the
    /// copy's value once (a value carried forward from last edition, or written by a path that only knew the sponsor record) —
    /// after that it is a page value like any other. The legal / billing name is never touched.
    /// </remarks>
    public static async Task<SyncResult> SyncFromCompanyPageAsync(CommunityHubDbContext db, int eventId, CancellationToken ct = default)
    {
        var pages = await db.ShopCompanies.Where(c => c.EventId == eventId).ToListAsync(ct);
        var pageById = pages.GroupBy(c => c.SponsorCompanyId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var infos = await db.SponsorInfos.IgnoreQueryFilters().Where(i => i.EventId == eventId).ToListAsync(ct);

        int rewritten = 0, absorbed = 0;
        foreach (var info in infos)
        {
            if (!pageById.TryGetValue(info.SponsorCompanyId, out var page)) continue;
            var copyChanged = false;

            void One(Func<string?> pageGet, Action<string?> pageSet, Func<string?> copyGet, Action<string?> copySet)
            {
                var p = Clean(pageGet());
                var c = Clean(copyGet());
                if (p is null && c is not null) { pageSet(c); absorbed++; p = c; }          // blank page: take the copy once
                if (!string.Equals(Clean(copyGet()), p, StringComparison.Ordinal)) { copySet(p); copyChanged = true; }
            }

            One(() => page.PublicName, v => page.PublicName = v, () => info.CompanyName, v => info.CompanyName = v);
            One(() => page.Website, v => page.Website = v, () => info.WebsiteUrl, v => info.WebsiteUrl = v);
            One(() => page.LinkedInUrl, v => page.LinkedInUrl = v, () => info.LinkedInUrl, v => info.LinkedInUrl = v);
            One(() => page.TwitterUrl, v => page.TwitterUrl = v, () => info.TwitterUrl, v => info.TwitterUrl = v);
            if (copyChanged) rewritten++;
        }
        if (rewritten > 0 || absorbed > 0) await db.SaveChangesAsync(ct);
        return new SyncResult(rewritten, absorbed);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>The shop company's links → the sponsor record (after the organizer saved them).</summary>
    public static async Task MirrorToInfoAsync(CommunityHubDbContext db, int eventId, string companyId, ShopCompany shop, CancellationToken ct = default)
    {
        var info = await db.SponsorInfos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.EventId == eventId && i.SponsorCompanyId == companyId, ct);
        if (info is null) return;
        info.WebsiteUrl = shop.Website;
        info.LinkedInUrl = shop.LinkedInUrl;
        info.TwitterUrl = shop.TwitterUrl;
    }
}
