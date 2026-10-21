using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>One person who can be chosen as signer or event coordinator on an order.</summary>
public sealed record ShopContactOption(int ParticipantId, string Name, string Email, bool IsSigner, bool IsEventCoordinator);

/// <summary>
/// §1305 — the CEH shop's view of a company: its legal/billing identity and its people.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Existing sponsors are picked up lazily</b> from their <see cref="SponsorInfo"/>; the billing identity then
/// follows e-conomic (<see cref="ShopErpRoleSyncService"/>). 🔴 §1312 — Company Manager is retired and no longer read.</para>
///
/// <para>🔑 <b>People come from CEH's existing links</b> (§1235 <see cref="SponsorCompanyContact"/>), which
/// the ERP/contact syncs keep in step with e-conomic — the master of people, as the operator requires
/// (<i>"it should come from erp which are the masters of people"</i>).</para>
/// </remarks>
public sealed class ShopCompanyService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShopCompanyService>? _log;

    public ShopCompanyService(
        CommunityHubDbContext db, TimeProvider clock,
        ILogger<ShopCompanyService>? log = null)
    {
        _db = db;
        _clock = clock;
        _log = log;
    }

    /// <summary>The shop company, creating it for an existing sponsor on first use. Null when unknown.</summary>
    public async Task<ShopCompany?> GetOrCreateAsync(int eventId, string sponsorCompanyId, CancellationToken ct = default)
    {
        var existing = await _db.ShopCompanies
            .FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == sponsorCompanyId, ct);
        if (existing is not null) return existing;

        // IgnoreQueryFilters: a SponsorInfo with IsSponsor=false is hidden by default, and it is still ours.
        var info = await _db.SponsorInfos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == sponsorCompanyId, ct);
        if (info is null) return null;

        var row = new ShopCompany
        {
            EventId = eventId,
            SponsorCompanyId = sponsorCompanyId,
            Source = ShopCompanySource.CompanyManager,
            State = info.IsSponsor ? ShopCompanyState.Sponsor : ShopCompanyState.Candidate,
            LegalName = info.CompanyName ?? $"Company {sponsorCompanyId}",
            PublicName = info.CompanyName,
            Website = info.WebsiteUrl,
            LinkedInUrl = info.LinkedInUrl,
            TwitterUrl = info.TwitterUrl,
            CreatedAt = _clock.GetUtcNow(),
        };

        _db.ShopCompanies.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>
    /// §1312 — keep the BILLING identity e-conomic owns in step (legal name, CVR / VAT, address, invoice e-mail, currency,
    /// VAT zone). Operator 2026-09-27: "can you confirm that we have a service that syncs automatically from erp to ceh in
    /// case i make changes in erp both billing fields + contacts + contact roles" — the fields were copied ONCE, when the
    /// company was created. 🔒 Only e-conomic's fields; the public name, website and social links are CEH's (§1311).
    /// 🔴 §1312 — e-conomic is the MASTER: every field is compared on every run and CEH follows it, A BLANK INCLUDED (operator
    /// 2026-10-01: "the erp sync must validate ALL fields at every sync … compare the fields and update if different"). It
    /// used to keep a CEH value when e-conomic's was blank, which kept a Company Manager placeholder "1000 København" on a UK
    /// company. Only a value CEH cannot hold is skipped: a blank legal name / currency (required) and an unrecognised country.
    /// Returns true when something changed.
    /// </summary>
    public static bool ApplyErpBilling(
        ShopCompany row, string? legalName, string? cvr, string? address, string? postcode, string? city, string? country,
        string? invoiceEmail, string? currency, int? vatZone)
    {
        var changed = false;
        void Mirror(string? value, Func<string?> get, Action<string?> set)
        {
            var v = Blank(value);
            if (!string.Equals(v, Blank(get()), StringComparison.Ordinal)) { set(v); changed = true; }
        }
        if (Blank(legalName) is { } name && name != row.LegalName) { row.LegalName = name; changed = true; }
        Mirror(Erp.LiveEconomicErpClient.NormaliseCvr(cvr), () => row.Cvr, v => row.Cvr = v);
        Mirror(address, () => row.Address, v => row.Address = v);
        Mirror(postcode, () => row.Postcode, v => row.Postcode = v);
        Mirror(city, () => row.City, v => row.City = v);
        if (CountryCodeMapper.ToIso2(country) is { } iso && iso != row.CountryIso2) { row.CountryIso2 = iso; changed = true; }
        Mirror(invoiceEmail, () => row.InvoiceEmail, v => row.InvoiceEmail = v);
        if (Blank(currency)?.ToUpperInvariant() is { } cur && cur != row.Currency) { row.Currency = cur; changed = true; }
        if (vatZone is int z and >= 1 and <= 4 && row.VatZone != z) { row.VatZone = z; changed = true; }

        // 🔴 §1312 — the ERP → CEH sync CREATES the public name: the ERP name without its legal form (ApS, A/S, Ltd …), and
        // ONLY while the public name is empty. Once filled — by this rule or by the operator — it is never changed again: the
        // rule is right ~9 times in 10 ("Admin By Request Nordics ApS" → the public name is "Admin By Request"), and the
        // operator's value wins (operator 2026-09-29, confirmed).
        if (string.IsNullOrWhiteSpace(row.PublicName) && Blank(legalName) is { } erpName)
        {
            row.PublicName = CompanyLegalForm.Strip(erpName);
            changed = true;
        }
        return changed;
    }

    /// <summary>The company's people, for the signer / coordinator dropdowns.</summary>
    public async Task<IReadOnlyList<ShopContactOption>> ContactsAsync(int eventId, string sponsorCompanyId, CancellationToken ct = default)
    {
        var links = await _db.SponsorCompanyContacts.AsNoTracking()
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == sponsorCompanyId)
            .Select(l => new { l.ParticipantId, l.IsSigner, l.IsEventCoordinator })
            .ToListAsync(ct);
        var ids = links.Select(l => l.ParticipantId).ToList();

        // People whose PRIMARY company this is but who have no link row yet (pre-§1235 data).
        var primary = await _db.Participants.AsNoTracking()
            .Where(p => p.EventId == eventId && p.SponsorCompanyId == sponsorCompanyId && p.IsActive)
            .Select(p => new { p.Id, p.FullName, p.Email, p.IsSigner, p.IsEventCoordinator })
            .ToListAsync(ct);
        var linked = await _db.Participants.AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.IsActive)
            .Select(p => new { p.Id, p.FullName, p.Email })
            .ToListAsync(ct);

        var result = new Dictionary<int, ShopContactOption>();
        foreach (var p in primary)
            result[p.Id] = new ShopContactOption(p.Id, p.FullName, p.Email, p.IsSigner, p.IsEventCoordinator);
        foreach (var p in linked)
        {
            var l = links.First(x => x.ParticipantId == p.Id);
            result[p.Id] = new ShopContactOption(p.Id, p.FullName, p.Email, l.IsSigner, l.IsEventCoordinator);
        }
        return result.Values.OrderByDescending(c => c.IsSigner).ThenBy(c => c.Name).ToList();
    }

    /// <summary>
    /// Save what a sponsor may change about their company from the shop: the announcement name, web and
    /// social links, and the invoice e-mail. Mirrored onto <see cref="SponsorInfo"/> so the public sponsor
    /// pages and SoMe announcements pick it up.
    /// </summary>
    /// <remarks>
    /// ⚠️ Billing identity (legal name, CVR, address, VAT zone) is NOT editable here: e-conomic is its
    /// master, and a sponsor-typed address would be overwritten by the next ERP sync anyway.
    /// </remarks>
    public async Task UpdateProfileAsync(
        int eventId, string sponsorCompanyId,
        string? publicName, string? website, string? linkedIn, string? twitter, string? invoiceEmail,
        CancellationToken ct = default)
    {
        var row = await GetOrCreateAsync(eventId, sponsorCompanyId, ct)
                  ?? throw new InvalidOperationException("Unknown company.");
        row.PublicName = Blank(publicName) ?? row.PublicName;
        row.Website = HttpsUrl.Normalize(website);   // §1312 — always https://
        row.LinkedInUrl = HttpsUrl.Normalize(linkedIn);
        row.TwitterUrl = HttpsUrl.Normalize(twitter);
        row.InvoiceEmail = Blank(invoiceEmail) ?? row.InvoiceEmail;
        row.UpdatedAt = _clock.GetUtcNow();

        var info = await _db.SponsorInfos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == sponsorCompanyId, ct);
        if (info is not null)
        {
            // 🔴 §1312 — the sponsor's own public-name edit reaches SoMe, Zoho and the site too (the doc comment above always
            // said so; the name was never copied, so it waited for the next order pull).
            if (!string.IsNullOrWhiteSpace(row.PublicName)) info.CompanyName = row.PublicName;
            info.WebsiteUrl = row.Website;
            info.LinkedInUrl = row.LinkedInUrl;
            info.TwitterUrl = row.TwitterUrl;
        }
        await _db.SaveChangesAsync(ct);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
