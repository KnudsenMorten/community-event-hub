using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Sponsors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// The Sponsor portal (REQUIREMENTS §20 Sponsor) — a single self-service home for a
/// signed-in sponsor: company profile + logo (resolved public name), booth/tier +
/// logistics quick-links, their leads (read), the deliverables checklist (the shared
/// <c>_ChecklistCard</c>), and order/invoice status from the ERP link entities.
///
/// All read-only / aggregation: the heavy lifting is in <see cref="SponsorPortalService"/>
/// (Core, unit-tested). Server-enforced role gate — only the Sponsor role reaches it,
/// and only ever for the company the signed-in sponsor is linked to.
/// </summary>
[Authorize]
public class PortalModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SponsorPortalService _portal;
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly ILogger<PortalModel> _log;

    public PortalModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SponsorPortalService portal,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        ILogger<PortalModel> log)
    {
        _db = db;
        _participant = participant;
        _portal = portal;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _log = log;
    }

    /// <summary>Set when a non-sponsor reaches the page (server-side gate, not CSS).</summary>
    public bool AccessDenied { get; private set; }

    /// <summary>Set when the sponsor's account has no linked company id yet.</summary>
    public bool NoCompanyLink { get; private set; }

    public SponsorPortalView? View { get; private set; }

    /// <summary>Greeting name for the page header.</summary>
    public string FirstName { get; private set; } = "there";

    // Logistics quick-links pulled from the event-edition config (same source as
    // /Sponsor/Logistics) so a different edition drops in its own URLs.
    public string? VenueFloorPlanUrl { get; private set; }
    public string? ExhibitorGuideUrl { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Server-enforced role gate — the portal is for the Sponsor role only.
        if (me.Role != ParticipantRole.Sponsor)
        {
            AccessDenied = true;
            return Page();
        }

        FirstName = me.FirstName;

        // Scope strictly to the signed-in sponsor's own company.
        var companyId = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(companyId))
        {
            NoCompanyLink = true;
            return Page();
        }

        View = await _portal.BuildAsync(me.EventId, me.ParticipantId, companyId!, ct);

        try
        {
            var cfg = _eventConfigLoader.Load(_eventConfigOptions.EventConfigPath);
            cfg.Placeholders.TryGetValue("venueFloorPlanUrl", out var v); VenueFloorPlanUrl = v;
            cfg.Placeholders.TryGetValue("exhibitorGuideUrl", out var g); ExhibitorGuideUrl = g;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sponsor/Portal: failed to load event-edition config for logistics links.");
        }

        return Page();
    }
}
