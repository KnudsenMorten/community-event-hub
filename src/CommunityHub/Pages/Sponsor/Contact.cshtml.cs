using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

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

    public ContactModel(
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

    public string LeadName  { get; private set; } = "Morten Knudsen";
    public string LeadEmail { get; private set; } = "mok@expertslive.dk";
    public string? BookingsUrl { get; private set; }
    public string EditionCode { get; private set; } = string.Empty;

    public IActionResult OnGet()
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

        return Page();
    }
}
