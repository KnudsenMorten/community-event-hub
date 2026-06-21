using CommunityHub.Core.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// "Contact Organizers" (operator 2026-06-21) — a single signed-in page that
/// surfaces how to reach the event organizers (replaces the "Email the organizers"
/// link that used to live under Resources). The address comes from the edition
/// config (<c>placeholders.organizerEmail</c>) so it stays per-edition; a sensible
/// fallback keeps the page useful if the placeholder is absent.
/// </summary>
[Authorize]
public class ContactModel : PageModel
{
    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _opt;

    public ContactModel(EventEditionConfigLoader cfg, EventConfigOptions opt)
    {
        _cfg = cfg;
        _opt = opt;
    }

    public string OrganizerEmail { get; private set; } = "info@expertslive.dk";

    public void OnGet()
    {
        try
        {
            var c = _cfg.Load(_opt.EventConfigPath);
            if (c.Placeholders.TryGetValue("organizerEmail", out var e) && !string.IsNullOrWhiteSpace(e))
                OrganizerEmail = e.Trim();
        }
        catch { /* keep the fallback address */ }
    }
}
