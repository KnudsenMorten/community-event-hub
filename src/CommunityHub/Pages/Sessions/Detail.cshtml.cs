using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace CommunityHub.Pages.Sessions;

/// <summary>
/// PUBLIC, no-login detail page for a single session (<c>/Sessions/{id}</c>,
/// REQUIREMENTS §21 PUBLIC). Shareable/SEO-friendly per-session page: title,
/// abstract, type/length/room/time, the linked speaker(s) (each cross-linked to
/// the public speaker page when that speaker is published — the same hard gate as
/// the lineup), and the session's deep-links: its master-class logistics page
/// (master class only) and its public "ask a question" page. Read-only.
///
/// Mobile-first (~360px) + a11y. 404s when there is no active event, the id is not
/// in the active edition, or it is a service session (breaks/lunch).
/// </summary>
[AllowAnonymous]
public class DetailModel : PageModel
{
    private readonly PublicSessionsService _svc;
    private readonly GraphicsService _graphics;
    private readonly string _defaultOgImagePath;

    // §196: relative path (under wwwroot) of the DEFAULT event/brand OG image used when a session
    // has no released graphic — so a shared /Sessions/{id} link ALWAYS cards an image, never blank.
    // Configurable via "Branding:DefaultOgImagePath"; falls back to the committed ELDK27 event logo.
    private const string DefaultOgImageFallback = "/img/logo-eldk27-event.png";

    public DetailModel(PublicSessionsService svc, GraphicsService graphics, IConfiguration config)
    {
        _svc = svc;
        _graphics = graphics;
        var configured = config["Branding:DefaultOgImagePath"];
        _defaultOgImagePath = string.IsNullOrWhiteSpace(configured) ? DefaultOgImageFallback : configured.Trim();
    }

    public PublicSessionDetail? Session { get; private set; }

    /// <summary>
    /// The ABSOLUTE, public URL of this session's OpenGraph image. §172: the released SoMe
    /// session-graphic served from the no-auth <c>/og/session-graphic/{id}</c> endpoint when one
    /// exists; §196: otherwise the configured DEFAULT event/brand OG image — so a shared
    /// <c>/Sessions/{id}</c> link ALWAYS cards an image (never blank). Always non-null for a
    /// resolved session.
    /// </summary>
    public string? OgImageUrl { get; private set; }

    /// <summary>§172: absolute canonical URL of this public session page (og:url).</summary>
    public string? OgUrl { get; private set; }

    /// <summary>§172: short, plain-text blurb for og:description / twitter:description.</summary>
    public string? OgDescription { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        Session = await _svc.GetByIdAsync(id, ct);
        if (Session is null) return NotFound();

        // §172: build the OpenGraph metadata so a shared /Sessions/{id} link renders a rich
        // card carrying the session's graphic. Absolute URLs from the current request (the
        // repo's {scheme}://{host} convention) — social crawlers need absolute https URLs.
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        OgUrl = $"{baseUrl}/Sessions/{id}";
        OgDescription = BuildOgDescription(Session);
        // Prefer this session's own released, stored graphic. §196: when it has none (e.g. the
        // "ELDK27 Welcome" session), fall back to the configured DEFAULT event/brand OG image so
        // the share card ALWAYS shows a graphic — never a blank card.
        OgImageUrl = await _graphics.HasPublicSessionOgGraphicAsync(id, ct)
            ? $"{baseUrl}/og/session-graphic/{id}"
            : $"{baseUrl}{_defaultOgImagePath}";

        return Page();
    }

    /// <summary>A short, single-line OG blurb: the abstract (trimmed) else a sensible fallback.</summary>
    private static string BuildOgDescription(PublicSessionDetail s)
    {
        var a = s.Abstract?.Trim();
        if (!string.IsNullOrEmpty(a))
        {
            // Collapse newlines and cap the length for a tidy social-card description.
            var oneLine = System.Text.RegularExpressions.Regex.Replace(a, @"\s+", " ");
            return oneLine.Length > 200 ? oneLine[..200].TrimEnd() + "…" : oneLine;
        }
        return $"{s.Title} at {s.EventDisplayName}.";
    }

    // --- Display helpers (shared labels with the overview page) -------------
    public static string Display(SessionType t) => IndexModel.Display(t);
    public static string Display(SessionLength l) => IndexModel.Display(l);
}
