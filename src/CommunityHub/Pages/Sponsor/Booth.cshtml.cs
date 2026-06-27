using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Venue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// "Our Booth" (REQUIREMENTS §146): a SPONSOR-only page showing the signed-in sponsor's
/// physical BOOTH NUMBER plus the EXPO map image(s). The booth number is the
/// <see cref="SponsorInfo.BoothLabel"/> for the sponsor's company (EventId + SponsorCompanyId) —
/// parsed from the booth product name during the order pull (§41a); a graceful "Booth TBD"
/// shows when no booth is assigned yet. The expo map is rendered LIVE from the SharePoint
/// Venue/Expo folder via the server-proxied <see cref="VenueImageProvider"/> (app creds; no
/// SharePoint link ever exposed), falling back to committed images / empty.
/// </summary>
[Authorize]
public class BoothModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly VenueImageProvider _venue;

    public BoothModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db,
        VenueImageProvider venue)
    {
        _participant = participant;
        _db = db;
        _venue = venue;
    }

    /// <summary>Set when a non-sponsor reaches the page (server-side gate, not CSS).</summary>
    public bool AccessDenied { get; private set; }

    /// <summary>The sponsor's physical booth number (e.g. "E-26"), or null when unassigned.</summary>
    public string? BoothNumber { get; private set; }

    /// <summary>True when the company holds a booth package (Gold+); false for digital-only.</summary>
    public bool HasBooth { get; private set; }

    /// <summary>The expo map / floor-plan images (server-proxied; may be empty).</summary>
    public IReadOnlyList<VenueGalleryImage> ExpoImages { get; private set; } =
        Array.Empty<VenueGalleryImage>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Server-enforced role gate — "Our Booth" is for the Sponsor role only.
        if (me.Role != ParticipantRole.Sponsor)
        {
            AccessDenied = true;
            return Page();
        }

        // Resolve the booth number from the sponsor's company SponsorInfo (booth label).
        var companyId = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(companyId))
        {
            var info = await _db.SponsorInfos
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.EventId == me.EventId && s.SponsorCompanyId == companyId, ct);
            if (info is not null)
            {
                HasBooth = info.HasBooth;
                BoothNumber = string.IsNullOrWhiteSpace(info.BoothLabel) ? null : info.BoothLabel!.Trim();
            }
        }

        // Expo map(s) — LIVE SharePoint via the server proxy, with committed/empty fallback.
        ExpoImages = await _venue.GetGalleryAsync("expo", ct);

        return Page();
    }
}
