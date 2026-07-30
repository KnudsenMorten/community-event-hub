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
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;
    private readonly ILogger<InfoPageModel> _logger;

    public InfoPageModel(
        ContentMarkdownRenderer renderer,
        ICurrentParticipantAccessor participant,
        VenueImageProvider venue,
        CommunityHub.Core.Settings.FeatureGateService gate,
        ILogger<InfoPageModel> logger)
    {
        _renderer = renderer;
        _participant = participant;
        _venue = venue;
        _gate = gate;
        _logger = logger;
    }

    /// <summary>The registry metadata for the requested slug (title, etc.).</summary>
    public ContentPage Content { get; private set; } = default!;

    /// <summary>§326br — the ROLE-AWARE heading for this page, so a speaker's menu entry
    /// ("… &amp; Speaker Hotel") and the page they land on agree (§318d align rule).</summary>
    public string PageTitle { get; private set; } = string.Empty;

    /// <summary>Rendered markdown body (raw HTML; trusted, in-repo content).</summary>
    public HtmlString BodyHtml { get; private set; } = HtmlString.Empty;

    /// <summary>
    /// Optional SPEAKER-only supplement appended to an all-roles page: the markdown file
    /// <c>config/content/&lt;edition&gt;/{slug}-speaker.md</c>, rendered ONLY for a Speaker (or an
    /// Organizer, who sees everything). Lets a shared page (e.g. Addresses) carry a speaker-only
    /// block — the speaker hotel — without exposing it to attendees/volunteers/sponsors. Empty
    /// when the viewer isn't a speaker or no supplement file exists.
    /// </summary>
    public HtmlString SpeakerSupplementHtml { get; private set; } = HtmlString.Empty;

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
        PageTitle = page.TitleFor(me.Role);

        // §351-5 (operator 2026-07-26: "dont include features which are turned off") — resolve the
        // page's [feature:key] directives against THIS edition's switches, so a row describing a
        // disabled capability is dropped from the markdown before it is rendered.
        var features = await _renderer.BuildFeatureLookupAsync(slug, _gate, me.EventId, ct);

        if (_renderer.TryRender(slug, out var html, publicView: false, features))
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

        // SPEAKER-only supplement ({slug}-speaker.md): rendered only for a Speaker (or an
        // Organizer, who sees everything). Keeps speaker-only blocks (e.g. the speaker hotel on
        // the all-roles Addresses page) hidden from attendees/volunteers/sponsors.
        if (me.Role is CommunityHub.Core.Domain.ParticipantRole.Speaker
                     or CommunityHub.Core.Domain.ParticipantRole.Organizer)
        {
            // The supplement is a separate file, so it gets its own §351-5 lookup.
            var supplementSlug = $"{slug}-speaker";
            var supplementFeatures =
                await _renderer.BuildFeatureLookupAsync(supplementSlug, _gate, me.EventId, ct);

            if (_renderer.TryRender(supplementSlug, out var speakerHtml, publicView: false, supplementFeatures))
            {
                SpeakerSupplementHtml = new HtmlString(speakerHtml);
            }
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
