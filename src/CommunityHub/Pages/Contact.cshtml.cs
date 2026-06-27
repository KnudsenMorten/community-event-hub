using CommunityHub.Content;
using CommunityHub.Core.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// "Contact Organizers" (operator 2026-06-21) — a single signed-in page that
/// surfaces how to reach the event organizers (replaces the "Email the organizers"
/// link that used to live under Resources). The address comes from the edition
/// config (<c>placeholders.organizerEmail</c>) so it stays per-edition; a sensible
/// fallback keeps the page useful if the placeholder is absent.
///
/// §107: the page also renders the organizer-team PROFILE cards (picture + role +
/// title + email) authored in <c>config/content/&lt;edition&gt;/organizers.md</c>,
/// reusing the same <see cref="ContentMarkdownRenderer"/> the generic /Info/{slug}
/// content pages use. The profile copy stays in the Markdown (operator-editable),
/// not in code.
/// </summary>
[Authorize]
public class ContactModel : PageModel
{
    // The organizer-profile content lives in organizers.md (NOT a registered
    // /Info/{slug} page — it is embedded here on the Contact page per §107).
    private const string OrganizersSlug = "organizers";

    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _opt;
    private readonly ContentMarkdownRenderer _renderer;

    public ContactModel(
        EventEditionConfigLoader cfg,
        EventConfigOptions opt,
        ContentMarkdownRenderer renderer)
    {
        _cfg = cfg;
        _opt = opt;
        _renderer = renderer;
    }

    public string OrganizerEmail { get; private set; } = "info@expertslive.dk";

    /// <summary>
    /// Rendered organizer-team profile cards (§107) from organizers.md, or empty
    /// when the file is absent (the page still shows the email fallback above).
    /// </summary>
    public HtmlString OrganizerProfilesHtml { get; private set; } = HtmlString.Empty;

    public void OnGet()
    {
        try
        {
            var c = _cfg.Load(_opt.EventConfigPath);
            if (c.Placeholders.TryGetValue("organizerEmail", out var e) && !string.IsNullOrWhiteSpace(e))
                OrganizerEmail = e.Trim();
        }
        catch { /* keep the fallback address */ }

        if (_renderer.TryRender(OrganizersSlug, out var html))
            OrganizerProfilesHtml = new HtmlString(html);
    }
}
