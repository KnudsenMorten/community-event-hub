using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sponsors;

/// <summary>
/// The order/invoice status of one sponsor company, projected from the hub-local
/// ERP link entities (<see cref="ErpCustomerLink"/> / <see cref="ErpOrderLink"/>,
/// REQUIREMENTS §7a). The live e-conomic + webshop write wiring is gated off until
/// operator credentials/endpoints are configured, so the projection reports honest
/// states — <c>Pending</c> when a link row exists but has no ERP number yet, and the
/// caller renders a "not configured" note when the ERP seam itself is gated — and
/// NEVER fabricates an invoice/amount.
/// </summary>
public sealed record SponsorOrderStatusRow(
    long WebshopOrderId,
    string ErpOrderNumber,
    string Currency,
    string? CurrencyCheckResult,
    DateTimeOffset CreatedAt)
{
    /// <summary>True once the order has an e-conomic order number (in the ERP).</summary>
    public bool InErp => !string.IsNullOrWhiteSpace(ErpOrderNumber);
}

/// <summary>
/// The sponsor-portal projection for ONE sponsor company. A read-only aggregate the
/// <c>/Sponsor/Portal</c> page renders: the resolved public company name (via the
/// shared <see cref="SponsorCompanyName"/> chain), tier/logo, the deliverables
/// checklist (the same <see cref="ParticipantChecklist"/> every surface renders), a
/// read view of their leads, the ERP customer/order link status, and the linked
/// contacts. It writes nothing.
/// </summary>
public sealed record SponsorPortalView(
    string CompanyId,
    string CompanyName,
    BoothTier Tier,
    string TierDisplay,
    /// <summary>Root-relative raster logo URL, or null → the page renders the monogram initials.</summary>
    string? LogoPath,
    string? WebsiteUrl,
    string Initials,
    ParticipantChecklist Checklist,
    int LeadCount,
    IReadOnlyList<SponsorLeadSummary> RecentLeads,
    /// <summary>True once the company has an e-conomic customer link with a customer number.</summary>
    bool ErpCustomerLinked,
    string? ErpCustomerNumber,
    IReadOnlyList<SponsorOrderStatusRow> Orders,
    IReadOnlyList<SponsorContact> Contacts);

/// <summary>A single lead row, read-only and trimmed to what a sponsor needs to see at a glance.</summary>
public sealed record SponsorLeadSummary(
    string FullName,
    string Company,
    SponsorLeadKind Kind,
    SponsorLeadStatus Status,
    DateTimeOffset CapturedAt);

/// <summary>One linked sponsor contact (booth staff) for the company.</summary>
public sealed record SponsorContact(string FullName, string Email, string? Phone);

/// <summary>
/// Builds the unified sponsor-portal view for the signed-in sponsor's company
/// (REQUIREMENTS §20 Sponsor — "Sponsor portal"). Read-only / aggregation only:
/// it reuses the existing seams (<see cref="SponsorCompanyName"/>,
/// <see cref="ParticipantChecklistBuilder"/>, the ERP link entities) rather than
/// duplicating logic, and is scoped strictly to the company id passed in so a
/// sponsor only ever sees their own company's data.
/// </summary>
public sealed class SponsorPortalService
{
    private readonly CommunityHubDbContext _db;
    private readonly ParticipantChecklistBuilder _checklist;

    public SponsorPortalService(CommunityHubDbContext db, ParticipantChecklistBuilder checklist)
    {
        _db = db;
        _checklist = checklist;
    }

    /// <summary>How many recent leads to surface on the portal (the rest live on the leads/API view).</summary>
    public const int RecentLeadsTake = 5;

    /// <summary>
    /// Build the portal for one company. <paramref name="participantId"/> drives the
    /// deliverables checklist (which already folds in the company-scoped sponsor
    /// tasks); <paramref name="companyId"/> scopes every other projection. Caller
    /// must have already verified the participant owns this company id.
    /// </summary>
    public async Task<SponsorPortalView> BuildAsync(
        int eventId, int participantId, string companyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("companyId is required", nameof(companyId));

        // Company facts (tier / logo / website) — one row per (event, company).
        var info = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && s.SponsorCompanyId == companyId)
            .Select(s => new { s.Tier, s.LogoRasterPath, s.WebsiteUrl })
            .FirstOrDefaultAsync(ct);

        // Public name: resolve through the shared fallback chain. The captured name
        // (order-pull time) is the public name; legal/billing are never the primary
        // (CLAUDE.md / DESIGN §6).
        var capturedName = await _db.SponsorUploadLocations
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == companyId
                        && l.CompanyName != string.Empty)
            .OrderBy(l => l.Id)
            .Select(l => l.CompanyName)
            .FirstOrDefaultAsync(ct);
        var name = SponsorCompanyName.Resolve(
            publicName: capturedName, legalName: null, billingName: null, companyId: companyId);

        // Deliverables checklist — the SAME ParticipantChecklist every surface shows.
        var checklist = await _checklist.BuildAsync(eventId, participantId, ct);

        // Leads (read): total + the most-recent few. Junk is hidden by default (matches
        // the sponsor download feed), but kept countable separately is unnecessary here.
        var visibleLeads = _db.SponsorLeads
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == companyId
                        && l.Status != SponsorLeadStatus.Junk);
        var leadCount = await visibleLeads.CountAsync(ct);
        var recent = await visibleLeads
            .OrderByDescending(l => l.CapturedAt)
            .Take(RecentLeadsTake)
            .Select(l => new SponsorLeadSummary(
                l.FullName, l.Company, l.LeadKind, l.Status, l.CapturedAt))
            .ToListAsync(ct);

        // ERP customer link (idempotency record). Empty ErpCustomerNumber = not yet
        // in the ERP (the live wiring is gated). We never fabricate a number.
        var erpCustomer = await _db.ErpCustomerLinks
            .Where(c => c.EventId == eventId && c.SponsorCompanyId == companyId)
            .Select(c => new { c.ErpCustomerNumber })
            .FirstOrDefaultAsync(ct);

        var orders = await _db.ErpOrderLinks
            .Where(o => o.EventId == eventId && o.SponsorCompanyId == companyId)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new SponsorOrderStatusRow(
                o.WebshopOrderId, o.ErpOrderNumber, o.Currency, o.CurrencyCheckResult, o.CreatedAt))
            .ToListAsync(ct);

        var contacts = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.SponsorCompanyId == companyId
                        && p.Role == ParticipantRole.Sponsor
                        && p.IsActive)
            .OrderBy(p => p.FullName)
            .Select(p => new SponsorContact(p.FullName, p.Email, p.Phone))
            .ToListAsync(ct);

        var erpNumber = erpCustomer?.ErpCustomerNumber;
        return new SponsorPortalView(
            CompanyId: companyId,
            CompanyName: name,
            Tier: info?.Tier ?? BoothTier.None,
            TierDisplay: TierDisplay(info?.Tier ?? BoothTier.None),
            LogoPath: NormalizeLogoPath(info?.LogoRasterPath),
            WebsiteUrl: SafeUrl(info?.WebsiteUrl),
            Initials: PublicInitials.From(name),
            Checklist: checklist,
            LeadCount: leadCount,
            RecentLeads: recent,
            ErpCustomerLinked: !string.IsNullOrWhiteSpace(erpNumber),
            ErpCustomerNumber: string.IsNullOrWhiteSpace(erpNumber) ? null : erpNumber,
            Orders: orders,
            Contacts: contacts);
    }

    /// <summary>Human-friendly tier name; mirrors the public sponsors page wording.</summary>
    public static string TierDisplay(BoothTier t) => t switch
    {
        BoothTier.Platinum => "Platinum",
        BoothTier.Diamond => "Diamond",
        BoothTier.Gold => "Gold",
        BoothTier.Feature => "Feature",
        _ => "Other supporter",
    };

    /// <summary>
    /// Logo paths are stored relative to wwwroot; normalise to a root-relative URL.
    /// Non-browser-renderable vector formats fall back to null (page shows monogram).
    /// Same rules as <c>PublicSponsorsService.NormalizeLogoPath</c>.
    /// </summary>
    private static string? NormalizeLogoPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('\\', '/');
        var ext = Path.GetExtension(p).ToLowerInvariant();
        if (ext is ".eps" or ".ai" or ".pdf") return null;
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
