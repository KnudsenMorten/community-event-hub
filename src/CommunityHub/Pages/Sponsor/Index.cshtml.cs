using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
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
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly WooCommerceClient _woo;
    private readonly WooCommerceOptions _wooOptions;
    private readonly IMemoryCache _cache;
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly ILogger<IndexModel> _log;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        CompanyManagerClient cm,
        CompanyManagerOptions cmOptions,
        WooCommerceClient woo,
        WooCommerceOptions wooOptions,
        IMemoryCache cache,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        ILogger<IndexModel> log)
    {
        _db = db;
        _participant = participant;
        _cm = cm;
        _cmOptions = cmOptions;
        _woo = woo;
        _wooOptions = wooOptions;
        _cache = cache;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _log = log;
    }

    public bool NoCompanyLink { get; private set; }
    public CompanyManagerCompany? CompanyDetails { get; private set; }
    public List<Participant> LinkedContacts { get; private set; } = new();
    public string ConfiguratorUrl { get; private set; } = string.Empty;
    public List<WooOrder> SponsorOrders { get; private set; } = new();

    /// <summary>Resolved booth code (e.g. "E-29") for booth sponsors; null for non-exhibitor sponsors.</summary>
    public string? AssignedBoothNumber { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

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

        var companyId = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
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

        if (_cmOptions.Enabled && int.TryParse(companyId, out var companyIdInt))
        {
            try
            {
                CompanyDetails = await _cm.GetCompanyAsync(companyIdInt, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sponsor/Index: Company Manager lookup failed for company {Co}.", companyIdInt);
            }
        }

        if (_wooOptions.Enabled)
        {
            try
            {
                var allOrders = await _cache.GetOrCreateAsync(
                    "woo:completed-orders",
                    async entry =>
                    {
                        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                        return await _woo.GetOrdersAsync("completed", ct);
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
