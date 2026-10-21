using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// The sponsor's "details" page -- everything ABOUT the sponsor company
/// (company facts from Company Manager, orders, linked contacts) but NOT
/// the task list. Task list lives at /Sponsor/Tasks as its own nav item.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHub.Core.Integrations.Webshop.IWebshopOrderSource _oldOrders;
    private readonly IMemoryCache _cache;
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly ILogger<IndexModel> _log;
    private readonly SponsorActingCompanyService _acting;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        CommunityHub.Core.Integrations.Webshop.IWebshopOrderSource oldOrders,
        IMemoryCache cache,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        ILogger<IndexModel> log,
        SponsorActingCompanyService acting)
    {
        _db = db;
        _participant = participant;
        _oldOrders = oldOrders;
        _cache = cache;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _log = log;
        _acting = acting;
    }

    /// <summary>Set when a non-sponsor reaches the page (server-side gate, not CSS).</summary>
    public bool AccessDenied { get; private set; }
    public bool NoCompanyLink { get; private set; }
    /// <summary>§1312 — CEH's own company record (Company Manager is retired): its names match group-photo registrations.</summary>
    public CommunityHub.Core.Domain.ShopCompany? CompanyDetails { get; private set; }

    // --- Self-service company profile (SponsorInfo, written by the Tasks
    // "Upload Company information" form) shown on the landing card. -----------
    /// <summary>One-liner shown above the company details (≤80 chars).</summary>
    public string? CompanyDescriptionShort { get; private set; }
    /// <summary>Longer company overview (≤1000 chars).</summary>
    public string? CompanyDescription { get; private set; }
    /// <summary>
    /// Root-relative raster-logo URL for an &lt;img&gt;, or null when no
    /// browser-renderable logo is on file. Source: SponsorInfo.LogoRasterPath
    /// (vector EPS/AI/PDF are intentionally dropped — not browser-renderable),
    /// via <c>PublicSponsorsService.PublicLogoPath</c> (a SharePoint logo is served through <c>/sponsor-logo/{company}</c>).
    /// </summary>
    public string? LogoPath { get; private set; }

    public List<Participant> LinkedContacts { get; private set; } = new();
    public string ConfiguratorUrl { get; private set; } = string.Empty;
    public List<WooOrder> SponsorOrders { get; private set; } = new();

    /// <summary>Resolved booth code (e.g. "E-29") for booth sponsors; null for non-exhibitor sponsors.</summary>
    public string? AssignedBoothNumber { get; private set; }

    // --- §234 UX: read-only "event activities" status (previously email/ICS-only) -----
    /// <summary>This company's attendee app-game participation (gift + confirmed flag);
    /// null when the organizers haven't registered the company for the game.</summary>
    public AppGameParticipation? AppGame { get; private set; }
    /// <summary>This company's group-photo registration (slot + location); null when no
    /// session is registered. Matched read-only by company name / contact email — the
    /// organizer-side table has no company-id column.</summary>
    public GroupPhotoRegistration? GroupPhoto { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Server-enforced role gate — the sponsor details page is for the Sponsor role only.
        if (me.Role != ParticipantRole.Sponsor)
        {
            AccessDenied = true;
            return Page();
        }

        try
        {
            var facts = _eventConfigLoader.Load(_eventConfigOptions.EventConfigPath);
            if (facts.Placeholders.TryGetValue("configuratorUrl", out var url))
            {
                ConfiguratorUrl = url ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sponsor/Index: failed to load event-edition config for configuratorUrl.");
        }

        // §1236 — the company this contact is ACTING for (switcher choice, re-validated), not just their primary.
        var companyId = await _acting.CurrentAsync(me.ParticipantId, me.EventId, ct);
        if (companyId is null)
        {
            NoCompanyLink = true;
            return Page();
        }

        LinkedContacts = await _db.Participants
            .Where(p => p.EventId == me.EventId
                        && p.SponsorCompanyId == companyId
                        && p.Role == ParticipantRole.Sponsor
                        && p.IsActive)
            .OrderBy(p => p.FullName)
            .ToListAsync(ct);

        // Self-service profile (short description + overview + raster logo) the
        // sponsor entered via the Tasks "Upload Company information" form. One
        // row per (event, company); null when nothing's been saved yet.
        var info = await _db.SponsorInfos
            .Where(s => s.EventId == me.EventId && s.SponsorCompanyId == companyId)
            .Select(s => new { s.CompanyDescriptionShort, s.CompanyDescription, s.LogoRasterPath })
            .FirstOrDefaultAsync(ct);
        if (info is not null)
        {
            CompanyDescriptionShort = info.CompanyDescriptionShort;
            CompanyDescription      = info.CompanyDescription;
            // §1315 — a SharePoint logo through the hub: a sponsor is not in the tenant, so the stored address is a 403 for them too.
            LogoPath                = CommunityHub.Core.Reminders.PublicSponsorsService.PublicLogoPath(companyId, info.LogoRasterPath);
        }


        CompanyDetails = await _db.ShopCompanies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EventId == me.EventId && c.SponsorCompanyId == companyId, ct);

        // §234 UX: surface the company's app-game + group-photo state read-only on the
        // portal (both were previously visible only via the emails / calendar invites the
        // organizer engine sends). Best-effort — a lookup failure never breaks the page.
        try
        {
            AppGame = await _db.AppGameParticipations
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.EventId == me.EventId
                                          && a.SponsorCompanyId == companyId, ct);

            // Group photos are registered by COMPANY NAME + lead-contact email (there is
            // no company-id column on the registration), so match by the company's known
            // names or any linked sponsor contact's email — in memory, the per-event
            // table is small.
            var contactEmails = LinkedContacts
                .Select(c => (c.Email ?? string.Empty).Trim().ToLowerInvariant())
                .Where(e => e.Length > 0)
                .ToHashSet();
            var companyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(CompanyDetails?.PublicName)) companyNames.Add(CompanyDetails!.PublicName.Trim());
            if (!string.IsNullOrWhiteSpace(CompanyDetails?.LegalName)) companyNames.Add(CompanyDetails!.LegalName.Trim());
            // §1312 — CEH's own names too: a company registered in the CEH shop has no Company Manager record.
            if (await Core.Integrations.SponsorCompanyNameService.LocalNameAsync(_db, companyId, ct) is { } localName) companyNames.Add(localName);
            if (await _db.ShopCompanies.AsNoTracking().Where(c => c.SponsorCompanyId == companyId)
                    .Select(c => c.LegalName).FirstOrDefaultAsync(ct) is { Length: > 0 } legal) companyNames.Add(legal.Trim());

            var photoRegs = await _db.GroupPhotoRegistrations
                .AsNoTracking()
                .Where(g => g.EventId == me.EventId)
                .ToListAsync(ct);
            GroupPhoto = photoRegs.FirstOrDefault(g =>
                companyNames.Contains((g.CompanyName ?? string.Empty).Trim())
                || contactEmails.Contains((g.ContactEmail ?? string.Empty).Trim().ToLowerInvariant()));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sponsor/Index: app-game / group-photo status lookup failed for company {Co}.", companyId);
        }

        // §1312 — the old webshop's orders from CEH's own store (always available; the webshop is closed).
        {
            try
            {
                var allOrders = await _cache.GetOrCreateAsync(
                    "woo:completed-orders",
                    async entry =>
                    {
                        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                        return await _oldOrders.GetOrdersAsync("completed", ct);   // §1312 — CEH's store of the old webshop's orders
                    }) ?? new List<WooOrder>();

                SponsorOrders = allOrders
                    .Where(o => string.Equals(o.CompanyId, companyId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(o => o.CreatedAt)
                    .ToList();

                // Resolve the assigned booth code from the first matching
                // line item across all of this company's orders. Same regex
                // as SponsorProductClassifier.BoothNumberRegex (kept in sync
                // intentionally -- this is a render-time read of the same
                // data the classifier uses to drive {{boothNumber}}).
                foreach (var ord in SponsorOrders)
                {
                    foreach (var item in ord.LineItems)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            item.ProductName ?? string.Empty,
                            @"\bE-(\d{1,3})\b",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            AssignedBoothNumber = "E-" + m.Groups[1].Value;
                            break;
                        }
                    }
                    if (AssignedBoothNumber is not null) break;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sponsor/Index: WooCommerce orders fetch failed for company {Co}.", companyId);
            }
        }

        return Page();
    }
}
