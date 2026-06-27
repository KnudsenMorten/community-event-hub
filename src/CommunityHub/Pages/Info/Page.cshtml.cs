using CommunityHub.Auth;
using CommunityHub.Content;
using CommunityHub.Core.Content;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Info;

/// <summary>
/// The generic CONTENT-HUB renderer (REQUIREMENTS §104–§123). One Razor page,
/// routed as <c>/Info/{slug}</c>, that renders an operator-authored markdown
/// file (<c>config/content/&lt;edition&gt;/{slug}.md</c>) inside the normal hub
/// layout. The <see cref="ContentPageRegistry"/> supplies the title and the
/// §123 role-gate: an unknown slug 404s, and a role that may not see the slug is
/// bounced back to the hub. New content pages are added by dropping a .md file +
/// a registry entry — no new Razor page needed.
/// </summary>
[Authorize]
public class InfoPageModel : PageModel
{
    private readonly ContentMarkdownRenderer _renderer;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ILogger<InfoPageModel> _logger;

    public InfoPageModel(
        ContentMarkdownRenderer renderer,
        ICurrentParticipantAccessor participant,
        ILogger<InfoPageModel> logger)
    {
        _renderer = renderer;
        _participant = participant;
        _logger = logger;
    }

    /// <summary>The registry metadata for the requested slug (title, etc.).</summary>
    public ContentPage Content { get; private set; } = default!;

    /// <summary>Rendered markdown body (raw HTML; trusted, in-repo content).</summary>
    public HtmlString BodyHtml { get; private set; } = HtmlString.Empty;

    /// <summary>True when the slug is registered but its .md file is missing.</summary>
    public bool ContentMissing { get; private set; }

    public IActionResult OnGet(string slug)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var page = ContentPageRegistry.Get(slug);
        if (page is null) return NotFound();

        // §123 role-gate: bounce a role that should not see this slug.
        if (!ContentPageRegistry.CanAccess(slug, me.Role))
        {
            _logger.LogInformation(
                "Info page '{Slug}' blocked for role {Role}; redirecting to hub.",
                slug, me.Role);
            return RedirectToPage("/Index");
        }

        Content = page;

        if (_renderer.TryRender(slug, out var html))
        {
            BodyHtml = new HtmlString(html);
        }
        else
        {
            // Registered but no content file yet — friendly empty state, not a 500.
            ContentMissing = true;
            _logger.LogWarning(
                "Info page '{Slug}' is registered but has no markdown file at {Path}.",
                slug, _renderer.ResolvePath(slug));
        }

        return Page();
    }
}
