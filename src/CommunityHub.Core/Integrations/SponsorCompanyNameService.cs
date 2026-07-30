using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Resolves sponsor company display names <b>from CEH SQL</b>, in ONE query, for a whole page.
///
/// <para>§443 (operator 2026-07-27: <i>"we should sync the data over using a sync routine. it must
/// newer pull data from company manager (cm) in the admin interface"</i>). Nine call sites used to
/// resolve the name by calling Company Manager — the WordPress plugin on the public event site —
/// <b>once per company, sequentially, while rendering</b>. On PROD that was ~12 round trips per
/// page view and made five organizer pages take 6–8 seconds, warm, every time. The cost tracked
/// the number of sponsor COMPANIES, so it grew with the event rather than with the page.</para>
///
/// <para><b>The data is already local.</b> <c>SponsorOrderPullService</c> — the CM → CEH sync
/// routine — resolves each company's name from Company Manager through
/// <see cref="SponsorCompanyName.Resolve"/> and persists it on
/// <c>SponsorUploadLocation.CompanyName</c>. The public <c>/Sponsors</c> page has always read that
/// local copy, which is why it renders in milliseconds. This service makes every other surface do
/// the same, so the names match by CONSTRUCTION (same CM fields, same resolve chain) rather than
/// by two code paths happening to agree.</para>
///
/// <para>A company the sync has not captured yet (one with no order, so no upload location was
/// ever provisioned) falls back to <c>"Company {id}"</c> — the documented last link of the chain.
/// That is a visible, honest gap rather than a silent 600 ms stall.</para>
/// </summary>
public sealed class SponsorCompanyNameService
{
    private readonly CommunityHubDbContext _db;

    public SponsorCompanyNameService(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// Every sponsor company name captured locally for the edition, as
    /// <c>companyId → display name</c>. ONE query, no external call. Ids with no captured name are
    /// simply absent — use <see cref="NameFor"/> (or <see cref="ResolveAllAsync"/>) to get the
    /// <c>"Company {id}"</c> fallback.
    /// </summary>
    public async Task<Dictionary<string, string>> GetCapturedNamesAsync(
        int eventId, CancellationToken ct = default)
    {
        // 🔒 §593 — `SponsorInfo.CompanyName` FIRST. It is the per-company home for the name,
        // synced from Company Manager's "Public Company Name" the moment the order pull resolves
        // it, and independent of any other subsystem.
        //
        // The SponsorUploadLocations lookup below is the LEGACY store and the direct cause of the
        // bug: that row is written only INSIDE SharePoint folder provisioning, so a company that
        // never got folders never got a name — /Organizer/Participants rendered
        // "(name not synced — CM id 30)" for a company CM knows as "System Center Dudes".
        // It is kept as a fallback so every name captured before this change keeps working with no
        // backfill; SponsorInfo wins wherever both exist.
        var infoNames = await _db.SponsorInfos
            .Where(i => i.EventId == eventId && i.CompanyName != null && i.CompanyName != "")
            .Select(i => new { i.SponsorCompanyId, i.CompanyName })
            .ToListAsync(ct);

        var map = infoNames
            .Where(r => !string.IsNullOrWhiteSpace(r.SponsorCompanyId))
            .GroupBy(r => r.SponsorCompanyId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().CompanyName!, StringComparer.Ordinal);

        var rows = await _db.SponsorUploadLocations
            .Where(l => l.EventId == eventId
                        && l.CompanyName != null
                        && l.CompanyName != "")
            .Select(l => new { l.SponsorCompanyId, l.CompanyName })
            .ToListAsync(ct);

        // A company can have SEVERAL upload locations (one per folder), all carrying the same
        // captured name — group so the dictionary build cannot throw on a duplicate key.
        foreach (var g in rows
                     .Where(r => !string.IsNullOrWhiteSpace(r.SponsorCompanyId))
                     .GroupBy(r => r.SponsorCompanyId, StringComparer.Ordinal))
        {
            if (!map.ContainsKey(g.Key)) map[g.Key] = g.First().CompanyName!;
        }

        // §593.4 — LAST RESORT: the ERP customer name (`ErpCustomerLinks.CompanyName`, written by
        // EconomicCustomerSyncService from the e-conomic customer record).
        //
        // This exists for the case Company Manager cannot serve: `SponsorOrderPullService` only
        // looks a company up in CM when its id is NUMERIC (`int.TryParse`), so a non-numeric id
        // such as "test-silver" NEVER gets a CM name, no matter how complete the CM record is.
        // When such a company exists in ERP, its customer name is a real, human name and is far
        // better than "(name not synced — CM id test-silver)".
        //
        // Ordered last deliberately: CM's "Public Company Name" is the operator-curated public
        // spelling ("Used in announcements and sponsor listings"), whereas the ERP name is the
        // accounting spelling. CM wins wherever both exist.
        var erp = await _db.ErpCustomerLinks
            .Where(l => l.EventId == eventId && l.CompanyName != "")
            .Select(l => new { l.SponsorCompanyId, l.CompanyName })
            .ToListAsync(ct);

        foreach (var g in erp
                     .Where(r => !string.IsNullOrWhiteSpace(r.SponsorCompanyId)
                                 && !string.IsNullOrWhiteSpace(r.CompanyName))
                     .GroupBy(r => r.SponsorCompanyId, StringComparer.Ordinal))
        {
            if (!map.ContainsKey(g.Key)) map[g.Key] = g.First().CompanyName!;
        }

        return map;
    }

    /// <summary>
    /// The display name for one company id, given an already-loaded lookup: the captured local
    /// name, else the <c>"Company {id}"</c> fallback. Pure — no I/O, safe to call per row.
    /// </summary>
    public static string NameFor(IReadOnlyDictionary<string, string> captured, string companyId)
    {
        captured.TryGetValue(companyId, out var local);
        return SponsorCompanyName.Resolve(local, legalName: null, billingName: null, companyId: companyId);
    }

    /// <summary>
    /// Convenience for a page that just wants "give me a name for each of these ids": one query,
    /// then the fallback applied for every id, so the caller never has to handle a miss.
    /// </summary>
    public async Task<Dictionary<string, string>> ResolveAllAsync(
        int eventId, IEnumerable<string> companyIds, CancellationToken ct = default)
    {
        var captured = await GetCapturedNamesAsync(eventId, ct);
        return companyIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => NameFor(captured, id), StringComparer.Ordinal);
    }

    /// <summary>
    /// The same resolution taking the <see cref="CommunityHubDbContext"/> directly, for the page
    /// models that already hold one. Deliberately static so replacing a live Company-Manager loop
    /// is a body swap and not a constructor change across nine pages — a smaller diff is a safer
    /// diff when the point of the change is to stop an outage-shaped dependency on the render path.
    /// </summary>
    public static Task<Dictionary<string, string>> ResolveFromLocalAsync(
        CommunityHubDbContext db, int eventId, IEnumerable<string> companyIds,
        CancellationToken ct = default) =>
        new SponsorCompanyNameService(db).ResolveAllAsync(eventId, companyIds, ct);
}
