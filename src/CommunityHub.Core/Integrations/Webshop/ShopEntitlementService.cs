using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>One company in a reservation-rights export.</summary>
public sealed class ShopEntitlementCompany
{
    [JsonPropertyName("sponsorCompanyId")] public string SponsorCompanyId { get; set; } = string.Empty;
    [JsonPropertyName("legalName")] public string LegalName { get; set; } = string.Empty;
    [JsonPropertyName("publicName")] public string? PublicName { get; set; }
    [JsonPropertyName("cvr")] public string? Cvr { get; set; }
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("postcode")] public string? Postcode { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("countryIso2")] public string CountryIso2 { get; set; } = "DK";
    [JsonPropertyName("currency")] public string Currency { get; set; } = "EUR";
    [JsonPropertyName("vatZone")] public int VatZone { get; set; }
    [JsonPropertyName("erpCustomerNumber")] public string? ErpCustomerNumber { get; set; }
    [JsonPropertyName("people")] public List<ShopEntitlementPerson> People { get; set; } = new();
    [JsonPropertyName("items")] public List<ShopEntitlementItem> Items { get; set; } = new();
}

public sealed class ShopEntitlementPerson
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("phone")] public string? Phone { get; set; }
    [JsonPropertyName("isSigner")] public bool IsSigner { get; set; }
    [JsonPropertyName("isEventCoordinator")] public bool IsEventCoordinator { get; set; }
}

public sealed class ShopEntitlementItem
{
    [JsonPropertyName("productName")] public string ProductName { get; set; } = string.Empty;
    [JsonPropertyName("boothId")] public string? BoothId { get; set; }
    [JsonPropertyName("timeslot")] public string? Timeslot { get; set; }
    [JsonPropertyName("group")] public string? Group { get; set; }
}

/// <summary>The whole export file.</summary>
public sealed class ShopEntitlementExport
{
    [JsonPropertyName("_schema")] public string Schema { get; set; } = "ceh/shop-entitlements@1";
    [JsonPropertyName("sourceEdition")] public string SourceEdition { get; set; } = string.Empty;
    [JsonPropertyName("exportedUtc")] public DateTimeOffset ExportedUtc { get; set; }
    [JsonPropertyName("companies")] public List<ShopEntitlementCompany> Companies { get; set; } = new();
}

/// <summary>What an import did.</summary>
public sealed record ShopEntitlementImportResult(int Companies, int CompaniesCreated, int People, int Items, int Matched, IReadOnlyList<string> Unmatched);

/// <summary>
/// §1305 item 10 — the reservation-rights "import module": export what each company had in one edition
/// (booths, sessions, branded packages — from BOTH shops), import it into the next edition's environment.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-26: <i>"For ELDK28, we want to open up, so sponsors can reserve the same options, they
/// had for ELDK27 related to booths and sessions (timeslots) … I want to deploy a complete new env for eldk28 …
/// clean database. I need to have a import module related to this so sponsors can reserve in eldk28 env, based
/// on their products in eldk27."</i></para>
///
/// <para>🔑 <b>A file, not a database link</b>, because the next edition lives in a separate environment with its
/// own database. The export carries the company's billing identity and its signer + coordinator, so the import
/// can create them there (as CANDIDATES: nobody is a sponsor of the new edition until they sign) — and the
/// weekly reminder has people to write to.</para>
///
/// <para>🔑 Matching to the new edition's products: booth id first (the expo layout is the same), then session
/// type + timeslot, then the product name without its edition code. Anything unmatched is listed for the
/// organizer and still reminded about ("the organizers will match it").</para>
/// </remarks>
public sealed class ShopEntitlementService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>The groups whose products carry over as reservation rights.</summary>
    public static readonly ShopGroup[] CarryOverGroups = { ShopGroup.Booths, ShopGroup.Sessions, ShopGroup.Branding, ShopGroup.Packages };

    private readonly CommunityHubDbContext _db;
    private readonly IWebshopOrderSource _orders;
    private readonly SponsorCompanyMembershipService _membership;
    private readonly TimeProvider _clock;

    public ShopEntitlementService(CommunityHubDbContext db, IWebshopOrderSource orders, SponsorCompanyMembershipService membership, TimeProvider clock)
    {
        _db = db;
        _orders = orders;
        _membership = membership;
        _clock = clock;
    }

    // ------------------------------------------------------------------------------------------
    // Export (run in the edition that is ending)
    // ------------------------------------------------------------------------------------------

    public async Task<string> ExportAsync(int eventId, string editionCode, CancellationToken ct = default)
    {
        // Which product (by webshop id) is in a carry-over group.
        var products = await _db.ShopProducts.AsNoTracking().Include(p => p.PrimaryCategory)
            .Where(p => p.EventId == eventId && p.WooProductId != null).ToListAsync(ct);
        var carry = products.Where(p => CarryOverGroups.Contains(p.PrimaryCategory?.Group ?? ShopGroup.Unassigned))
            .ToDictionary(p => p.WooProductId!.Value);

        var byCompany = new Dictionary<string, List<ShopEntitlementItem>>(StringComparer.Ordinal);
        void Add(string companyId, string name, ShopProduct? product)
        {
            if (!byCompany.TryGetValue(companyId, out var list)) byCompany[companyId] = list = new();
            var booth = product?.BoothId ?? ShopProductNameParser.BoothId(name);
            var (slot, _) = ShopProductNameParser.Timeslot(name);
            if (list.Any(x => x.ProductName == name)) return;
            list.Add(new ShopEntitlementItem
            {
                ProductName = name, BoothId = booth, Timeslot = product?.Timeslot ?? slot,
                Group = product?.PrimaryCategory?.Group.ToString(),
            });
        }

        // The old webshop's completed orders.
        foreach (var o in (await _orders.GetOrdersAsync("completed", ct, enrichCategories: false)).Where(o => !string.IsNullOrWhiteSpace(o.CompanyId)))
            foreach (var l in o.LineItems)
                if (carry.TryGetValue(l.ProductId, out var p)) Add(o.CompanyId!, l.ProductName, p);

        // Signed CEH orders.
        var signed = await _db.ShopOrders.AsNoTracking()
            .Where(o => o.EventId == eventId && o.Status == ShopOrderStatus.Signed)
            .Include(o => o.Lines).ThenInclude(l => l.Product).ThenInclude(p => p.PrimaryCategory)
            .ToListAsync(ct);
        foreach (var o in signed)
            foreach (var l in o.Lines.Where(l => CarryOverGroups.Contains(l.Product.PrimaryCategory?.Group ?? ShopGroup.Unassigned)))
                Add(o.SponsorCompanyId, l.ProductName, l.Product);

        var export = new ShopEntitlementExport { SourceEdition = editionCode, ExportedUtc = _clock.GetUtcNow() };
        foreach (var (companyId, items) in byCompany)
        {
            var shop = await _db.ShopCompanies.AsNoTracking().FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct);
            var info = await _db.SponsorInfos.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
            var linkIds = await _db.SponsorCompanyContacts.AsNoTracking()
                .Where(l => l.EventId == eventId && l.SponsorCompanyId == companyId && (l.IsSigner || l.IsEventCoordinator))
                .ToListAsync(ct);
            var ids = linkIds.Select(l => l.ParticipantId).ToList();
            var people = await _db.Participants.AsNoTracking().Where(p => ids.Contains(p.Id) && p.IsActive).ToListAsync(ct);

            export.Companies.Add(new ShopEntitlementCompany
            {
                SponsorCompanyId = companyId,
                LegalName = shop?.LegalName ?? info?.CompanyName ?? companyId,
                PublicName = shop?.PublicName ?? info?.CompanyName,
                Cvr = shop?.Cvr, Address = shop?.Address, Postcode = shop?.Postcode, City = shop?.City,
                CountryIso2 = shop?.CountryIso2 ?? "DK", Currency = shop?.Currency ?? "EUR", VatZone = shop?.VatZone ?? 0,
                ErpCustomerNumber = shop?.ErpCustomerNumber,
                People = people.Select(p =>
                {
                    var l = linkIds.First(x => x.ParticipantId == p.Id);
                    return new ShopEntitlementPerson { Name = p.FullName, Email = p.Email, Phone = p.Phone, IsSigner = l.IsSigner, IsEventCoordinator = l.IsEventCoordinator };
                }).ToList(),
                Items = items,
            });
        }
        return JsonSerializer.Serialize(export, Json);
    }

    // ------------------------------------------------------------------------------------------
    // Import (run in the new edition's environment)
    // ------------------------------------------------------------------------------------------

    public async Task<ShopEntitlementImportResult> ImportAsync(int eventId, string json, CancellationToken ct = default)
    {
        var export = JsonSerializer.Deserialize<ShopEntitlementExport>(json, Json)
                     ?? throw new InvalidDataException("The file is not a reservation-rights export.");
        var now = _clock.GetUtcNow();
        var products = await _db.ShopProducts.AsNoTracking().Include(p => p.PrimaryCategory)
            .Where(p => p.EventId == eventId && p.Status != ShopProductStatus.Removed).ToListAsync(ct);

        int created = 0, people = 0, items = 0, matched = 0;
        var unmatched = new List<string>();

        foreach (var c in export.Companies)
        {
            var shop = await _db.ShopCompanies.FirstOrDefaultAsync(x => x.EventId == eventId && x.SponsorCompanyId == c.SponsorCompanyId, ct);
            if (shop is null)
            {
                shop = new ShopCompany
                {
                    EventId = eventId, SponsorCompanyId = c.SponsorCompanyId,
                    Source = c.SponsorCompanyId.StartsWith("ceh-", StringComparison.Ordinal) ? ShopCompanySource.Registered : ShopCompanySource.CompanyManager,
                    State = ShopCompanyState.Candidate, LegalName = c.LegalName, PublicName = c.PublicName,
                    Cvr = c.Cvr, Address = c.Address, Postcode = c.Postcode, City = c.City, CountryIso2 = c.CountryIso2,
                    Currency = c.Currency, VatZone = c.VatZone, ErpCustomerNumber = c.ErpCustomerNumber, CreatedAt = now,
                };
                _db.ShopCompanies.Add(shop);
                created++;
            }
            if (!await _db.SponsorInfos.IgnoreQueryFilters().AnyAsync(s => s.EventId == eventId && s.SponsorCompanyId == c.SponsorCompanyId, ct))
                _db.SponsorInfos.Add(new SponsorInfo { EventId = eventId, SponsorCompanyId = c.SponsorCompanyId, CompanyName = (string.IsNullOrWhiteSpace(c.PublicName) ? CompanyLegalForm.Strip(c.LegalName) : c.PublicName), IsSponsor = false, CreatedAt = now });
            await _db.SaveChangesAsync(ct);

            foreach (var p in c.People.Where(p => !string.IsNullOrWhiteSpace(p.Email)))
            {
                var email = p.Email.Trim().ToLowerInvariant();
                var person = await _db.Participants.FirstOrDefaultAsync(x => x.EventId == eventId && x.Email == email, ct);
                if (person is null)
                {
                    person = new Participant
                    {
                        EventId = eventId, Email = email, FullName = p.Name, Phone = p.Phone, Role = ParticipantRole.Sponsor,
                        SponsorCompanyId = c.SponsorCompanyId, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
                        IsSigner = p.IsSigner, IsEventCoordinator = p.IsEventCoordinator, CreatedAt = now,
                    };
                    _db.Participants.Add(person);
                    await _db.SaveChangesAsync(ct);
                    people++;
                }
                await _membership.LinkAsync(eventId, person.Id, c.SponsorCompanyId, isSigner: p.IsSigner,
                    isEventCoordinator: p.IsEventCoordinator, isPrimary: person.SponsorCompanyId == c.SponsorCompanyId, ct: ct);
            }

            foreach (var item in c.Items)
            {
                if (await _db.ShopEntitlements.AnyAsync(e => e.EventId == eventId && e.SponsorCompanyId == c.SponsorCompanyId
                        && e.ProductName == item.ProductName && e.SourceEdition == export.SourceEdition, ct))
                    continue;
                var match = Match(item, products);
                if (match is not null) matched++; else unmatched.Add($"{c.LegalName}: {item.ProductName}");
                _db.ShopEntitlements.Add(new ShopEntitlement
                {
                    EventId = eventId, SponsorCompanyId = c.SponsorCompanyId, CompanyName = (string.IsNullOrWhiteSpace(c.PublicName) ? CompanyLegalForm.Strip(c.LegalName) : c.PublicName),
                    SourceEdition = export.SourceEdition, ProductName = item.ProductName, BoothId = item.BoothId,
                    Timeslot = item.Timeslot, MatchedProductId = match?.Id, ImportedAt = now,
                });
                items++;
            }
            await _db.SaveChangesAsync(ct);
        }
        return new ShopEntitlementImportResult(export.Companies.Count, created, people, items, matched, unmatched);
    }

    /// <summary>
    /// The product in the new edition that corresponds to what a company had: booth id, then session type +
    /// timeslot, then the name without edition codes. Null when nothing matches — never a guess.
    /// </summary>
    public static ShopProduct? Match(ShopEntitlementItem item, IReadOnlyList<ShopProduct> products)
    {
        if (!string.IsNullOrWhiteSpace(item.BoothId))
        {
            var byBooth = products.Where(p => string.Equals(p.BoothId, item.BoothId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byBooth.Count == 1) return byBooth[0];
            var sameKind = byBooth.FirstOrDefault(p => Stem(p.Name) == Stem(item.ProductName));
            if (sameKind is not null) return sameKind;
            if (byBooth.Count > 0) return byBooth[0];
        }
        if (!string.IsNullOrWhiteSpace(item.Timeslot))
        {
            var kind = SessionKind(item.ProductName);
            var bySlot = products.FirstOrDefault(p => string.Equals(p.Timeslot, item.Timeslot, StringComparison.OrdinalIgnoreCase)
                                                      && SessionKind(p.Name) == kind);
            if (bySlot is not null) return bySlot;
        }
        var stem = Stem(item.ProductName);
        return products.FirstOrDefault(p => Stem(p.Name) == stem);
    }

    /// <summary>A product name without edition codes, punctuation and case: "Coffee Sponsor ELDK27" → "coffee sponsor".</summary>
    public static string Stem(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name ?? string.Empty, @"\bELDK\d{2,4}\b", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\(est\.?\s*time:[^)]*\)", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^\p{L}\p{N}]+", " ");
        return s.Trim().ToLowerInvariant();
    }

    private static string SessionKind(string name)
    {
        var n = (name ?? string.Empty).ToLowerInvariant();
        if (n.Contains("20 min")) return "20";
        if (n.Contains("50 min")) return "50";
        if (n.Contains("60 min")) return "60";
        if (n.Contains("30 min")) return "30";
        return "?";
    }
}
