using CommunityHub.Auth;
using CommunityHub.Content;
using CommunityHub.Core.Content;
using CommunityHub.Venue;
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
    private readonly VenueImageProvider _venue;
    private readonly ILogger<InfoPageModel> _logger;

    public InfoPageModel(
        ContentMarkdownRenderer renderer,
        ICurrentParticipantAccessor participant,
        VenueImageProvider venue,
        ILogger<InfoPageModel> logger)
    {
        _renderer = renderer;
        _participant = participant;
        _venue = venue;
        _logger = logger;
    }

    /// <summary>The registry metadata for the requested slug (title, etc.).</summary>
    public ContentPage Content { get; private set; } = default!;

    /// <summary>Rendered markdown body (raw HTML; trusted, in-repo content).</summary>
    public HtmlString BodyHtml { get; private set; } = HtmlString.Empty;

    /// <summary>True when the slug is registered but its .md file is missing.</summary>
    public bool ContentMissing { get; private set; }

    /// <summary>
    /// LIVE SharePoint venue images for slugs with a mapped Venue folder (§146):
    /// wayfinding / good-to-know / session-evaluations. Empty for other slugs (no gallery
    /// rendered) or when neither SharePoint nor a committed fallback has any image.
    /// </summary>
    public IReadOnlyList<VenueGalleryImage> Gallery { get; private set; } =
        System.Array.Empty<VenueGalleryImage>();

    public async Task<IActionResult> OnGetAsync(string slug, System.Threading.CancellationToken ct)
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

        // §146: append the LIVE SharePoint venue gallery for slugs with a mapped folder
        // (the markdown text is kept). Fail-soft: a gallery hiccup never breaks the page.
        var folderKey = _venue.FolderForSlug(slug);
        if (folderKey is not null)
        {
            try
            {
                Gallery = await _venue.GetGalleryAsync(folderKey, ct);
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning(ex, "Info page '{Slug}': venue gallery load failed.", slug);
            }
        }

        return Page();
    }
}
