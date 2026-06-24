using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// On-site logistics quick-links for sponsors: venue / floor plan, exhibitor
/// guide PDF, freight contact. Pulls every link from event.&lt;edition&gt;.json
/// placeholders so a different edition or community drops in its own URLs. The
/// page is personalised with the sponsor's real company name (Company Manager
/// public name) so it reads "<i>Acme</i> — your booth run-of-show", not a bare
/// generic page (operator 2026-06-24).
/// </summary>
[Authorize]
public class LogisticsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly CommunityHubDbContext _db;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly ILogger<LogisticsModel> _log;

    public LogisticsModel(
        ICurrentParticipantAccessor participant,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        CommunityHubDbContext db,
        CompanyManagerClient cm,
        CompanyManagerOptions cmOptions,
        ILogger<LogisticsModel> log)
    {
        _participant = participant;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _db = db;
        _cm = cm;
        _cmOptions = cmOptions;
        _log = log;
    }

    /// <summary>Set when a non-sponsor reaches the page (server-side gate, not CSS).</summary>
    public bool AccessDenied { get; private set; }

    /// <summary>The sponsor's real company name (CM public name, falling back to legal name).</summary>
    public string? CompanyDisplayName { get; private set; }

    public string? ExhibitorGuideUrl { get; private set; }
    public string? VenueFloorPlanUrl { get; private set; }
    public string? FreightContactPhone { get; private set; }
    public string? FreightContactEmail { get; private set; }
    public string? ShippingAddressDsv { get; private set; }
    public string? ShippingAddressBags { get; private set; }
    public string EditionCode { get; private set; } = string.Empty;

    /// <summary>Key-dates panel data (same source/shape Tasks used before the panel moved here).</summary>
    public EditionDates? EventDates { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Server-enforced role gate — the sponsor logistics page is for the Sponsor role only.
        if (me.Role != ParticipantRole.Sponsor)
        {
            AccessDenied = true;
            return Page();
        }

        // Personalise with the sponsor's real company name (Company Manager public
        // name → legal name). Fail-soft: a CM hiccup leaves the page generic, never
        // shows "Company {id}".
        var companyId = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        if (_cmOptions.Enabled && int.TryParse(companyId, out var companyIdInt))
        {
            try
            {
                var company = await _cm.GetCompanyAsync(companyIdInt, ct);
                if (company is not null)
                {
                    CompanyDisplayName = !string.IsNullOrWhiteSpace(company.PublicName)
                        ? company.PublicName
                        : company.Name;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sponsor/Logistics: Company Manager lookup failed for company {Co}.", companyIdInt);
            }
        }

        var cfg = _eventConfigLoader.Load(_eventConfigOptions.EventConfigPath);
        EditionCode = cfg.Code ?? string.Empty;
        // Key dates panel data — same single-read load Tasks used (the partial
        // tolerates a null model). Moved here from Sponsor/Tasks.
        EventDates = cfg.Dates;
        cfg.Placeholders.TryGetValue("exhibitorGuideUrl",   out var g); ExhibitorGuideUrl   = g;
        cfg.Placeholders.TryGetValue("venueFloorPlanUrl",   out var v); VenueFloorPlanUrl   = v;
        cfg.Placeholders.TryGetValue("freightContactPhone", out var fp); FreightContactPhone = fp;
        cfg.Placeholders.TryGetValue("freightContactEmail", out var fe); FreightContactEmail = fe;
        cfg.Placeholders.TryGetValue("shippingAddressDsv",  out var sa); ShippingAddressDsv  = sa;
        cfg.Placeholders.TryGetValue("shippingAddressBags", out var sb); ShippingAddressBags = sb;

        return Page();
    }
}
