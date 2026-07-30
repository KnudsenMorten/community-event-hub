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

    /// <summary>The sponsor's physical booth number (e.g. "E-29"), or null when unassigned.</summary>
    public string? BoothNumber { get; private set; }

    /// <summary>
    /// The company token used on the "Mark every box with: …" lines (§174). Uses the
    /// resolved public/legal name; degrades to a soft "your company" (never the literal
    /// "&lt;your company&gt;" placeholder) when Company Manager is off or the lookup failed.
    /// </summary>
    public string BoxMarkingCompany => ResolveBoxMarkingCompany(CompanyDisplayName);

    /// <summary>
    /// The DSV freight box-marking line (§174b): "&lt;edition&gt; / Booth &lt;number&gt; / &lt;company&gt;"
    /// (e.g. "ELDK27 / Booth E-29 / 2LINKIT"). Degrades to "Booth (TBA)" when no booth is
    /// assigned yet, mirroring the "Booth TBD" graceful state on the Our Booth page.
    /// </summary>
    public string DsvBoxMarking => ResolveDsvBoxMarking(EditionCode, BoothNumber, CompanyDisplayName);

    /// <summary>
    /// Pure box-marking company token (§174a) — the resolved public/legal name, or the soft
    /// "your company" fallback (never the literal "&lt;your company&gt;" placeholder).
    /// </summary>
    public static string ResolveBoxMarkingCompany(string? companyDisplayName) =>
        !string.IsNullOrWhiteSpace(companyDisplayName) ? companyDisplayName! : "your company";

    /// <summary>
    /// Pure DSV freight box-marking line (§174b) — "&lt;edition&gt; / Booth &lt;number&gt; / &lt;company&gt;",
    /// degrading the booth to "Booth (TBA)" when unassigned and the company via
    /// <see cref="ResolveBoxMarkingCompany"/>.
    /// </summary>
    public static string ResolveDsvBoxMarking(string editionCode, string? boothNumber, string? companyDisplayName) =>
        $"{editionCode} / {(string.IsNullOrWhiteSpace(boothNumber) ? "Booth (TBA)" : $"Booth {boothNumber!.Trim()}")} / {ResolveBoxMarkingCompany(companyDisplayName)}";

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

        // Booth number for the DSV box-marking line (§174b) — the SAME source the Our
        // Booth page uses: SponsorInfo.BoothLabel for this company (EventId +
        // SponsorCompanyId), parsed from the booth product during the order pull. Stays
        // null (→ "Booth (TBA)") when no booth is assigned yet.
        if (!string.IsNullOrWhiteSpace(companyId))
        {
            var info = await _db.SponsorInfos
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.EventId == me.EventId && s.SponsorCompanyId == companyId, ct);
            if (info is not null && !string.IsNullOrWhiteSpace(info.BoothLabel))
                BoothNumber = info.BoothLabel!.Trim();
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
