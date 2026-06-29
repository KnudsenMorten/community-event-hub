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

    // Neutral, no-real-name fallbacks. The real lead name/email come from the
    // edition config placeholders (leadContactName / leadContactEmail) in
    // OnGetAsync; these defaults only apply when an edition hasn't set them, so
    // we never bake a personal name/address into the code.
    public string LeadName  { get; private set; } = "Sponsor Team";
    public string LeadEmail { get; private set; } = string.Empty;
    public string? BookingsUrl { get; private set; }
    public string EditionCode { get; private set; } = string.Empty;

    public IActionResult OnGet()
    {
        // Unified contact page (operator 2026-06-28): every role — including sponsors —
        // uses the same shared /Contact page now, so this old sponsor-specific route just
        // redirects there (bookmarks/old links keep working).
        if (_participant.Current is null) return RedirectToPage("/Login");
        return RedirectToPage("/Contact");
    }
}
