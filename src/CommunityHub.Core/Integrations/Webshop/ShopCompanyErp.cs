using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// 🔴 §1312 — a company's e-conomic customer number as the CEH shop holds it (<see cref="Domain.ShopCompany.ErpCustomerNumber"/>,
/// stamped at registration). The sponsor pages asked Company Manager only, so a company registered in the CEH shop
/// (<c>ceh-…</c>, no Company Manager record) could not see or manage its e-conomic contacts (audit 2026-09-29; operator:
/// "we are not pulling orders from CM anymore, we use ceh new webshop"). Callers try this first, Company Manager after.
/// </summary>
public static class ShopCompanyErp
{
    /// <summary>The customer number, or null when the shop has none for this company.</summary>
    public static async Task<int?> CustomerNumberAsync(CommunityHubDbContext db, string companyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId)) return null;
        var numbers = await db.ShopCompanies.AsNoTracking()
            .Where(c => c.SponsorCompanyId == companyId && c.ErpCustomerNumber != null && c.ErpCustomerNumber != "")
            .OrderByDescending(c => c.EventId)
            .Select(c => c.ErpCustomerNumber)
            .ToListAsync(ct);
        foreach (var n in numbers)
            if (int.TryParse(n, out var number) && number > 0) return number;
        return null;
    }
}
