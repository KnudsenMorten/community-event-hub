using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// Sponsor contact page: mailto for the sponsor lead + (optional) Teams
/// meeting request via Microsoft Bookings. The Bookings URL is editorial --
/// set <c>placeholders.bookingsUrl</c> in event.&lt;edition&gt;.json; empty =
/// the Teams meeting button is hidden so we never link to a half-configured
/// flow.
/// </summary>
[Authorize]
public class ContactModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly CommunityHubDbContext _db;

    public ContactModel(
        ICurrentParticipantAccessor participant,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        CommunityHubDbContext db)
    {
        _participant = participant;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _db = db;
    }

    /// <summary>The sponsor's company display name, for the mailto subject.</summary>
    public string? CompanyName { get; private set; }

    /// <summary>Set when a non-sponsor reaches the page (server-side gate, not CSS).</summary>
    public bool AccessDenied { get; private set; }

    public string LeadName  { get; private set; } = "Morten Knudsen";
    public string LeadEmail { get; private set; } = "mok@expertslive.dk";
    public string? BookingsUrl { get; private set; }
    public string EditionCode { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Server-enforced role gate — the sponsor contact page is for the Sponsor role only.
        if (me.Role != ParticipantRole.Sponsor)
        {
            AccessDenied = true;
            return Page();
        }

        var cfg = _eventConfigLoader.Load(_eventConfigOptions.EventConfigPath);
        EditionCode = cfg.Code ?? string.Empty;
        if (cfg.Placeholders.TryGetValue("leadContactName", out var n) && !string.IsNullOrWhiteSpace(n)) LeadName = n;
        if (cfg.Placeholders.TryGetValue("leadContactEmail", out var e) && !string.IsNullOrWhiteSpace(e)) LeadEmail = e;
        if (cfg.Placeholders.TryGetValue("bookingsUrl", out var b) && !string.IsNullOrWhiteSpace(b)) BookingsUrl = b;

        // Resolve the sponsor's company display name for the mailto subject
        // (so the lead sees "from <Company>"). Same source the pull stamps.
        var companyId = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(companyId))
        {
            CompanyName = await _db.SponsorUploadLocations
                .Where(l => l.EventId == me.EventId && l.SponsorCompanyId == companyId
                            && l.CompanyName != null && l.CompanyName != string.Empty)
                .Select(l => l.CompanyName)
                .FirstOrDefaultAsync(ct);
        }

        return Page();
    }
}
