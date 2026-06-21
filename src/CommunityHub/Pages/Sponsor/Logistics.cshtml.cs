using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// On-site logistics quick-links for sponsors: venue / floor plan, exhibitor
/// guide PDF, freight contact. Pulls every link from event.&lt;edition&gt;.json
/// placeholders so a different edition or community drops in its own URLs.
/// </summary>
[Authorize]
public class LogisticsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;

    public LogisticsModel(
        ICurrentParticipantAccessor participant,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions)
    {
        _participant = participant;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
    }

    /// <summary>Set when a non-sponsor reaches the page (server-side gate, not CSS).</summary>
    public bool AccessDenied { get; private set; }

    public string? ExhibitorGuideUrl { get; private set; }
    public string? VenueFloorPlanUrl { get; private set; }
    public string? FreightContactPhone { get; private set; }
    public string? FreightContactEmail { get; private set; }
    public string? ShippingAddressDsv { get; private set; }
    public string EditionCode { get; private set; } = string.Empty;

    /// <summary>Key-dates panel data (same source/shape Tasks used before the panel moved here).</summary>
    public EditionDates? EventDates { get; private set; }

    public IActionResult OnGet()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Server-enforced role gate — the sponsor logistics page is for the Sponsor role only.
        if (me.Role != ParticipantRole.Sponsor)
        {
            AccessDenied = true;
            return Page();
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

        return Page();
    }
}
