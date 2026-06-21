using System;
using System.Collections.Generic;
using System.Linq;

namespace CommunityHub.Core.Navigation;

/// <summary>
/// One ancestor crumb in a breadcrumb trail. <see cref="LabelKey"/> is a
/// SharedResource resx KEY (not a localized string) so the view layer owns i18n
/// via <c>@Localizer</c>, mirroring <see cref="NavItem"/>. <see cref="Href"/> is
/// the route the crumb links to.
/// </summary>
public sealed record BreadcrumbCrumb(string Href, string LabelKey);

/// <summary>
/// Pure, server-side breadcrumb composition for the organizer back-office
/// (REQUIREMENTS §21 cross-cutting "breadcrumbs"). Given a request path it
/// returns the ordered ANCESTOR trail — e.g. for <c>/Organizer/Participants</c>:
/// <c>Organizer area ▸ People</c> — NOT including the current page. The view
/// renders these ancestors as links and appends the current page's
/// <c>ViewData["Title"]</c> as the non-linked leaf, so no per-page wiring is
/// needed and the leaf label always matches the page title already set.
///
/// The organizer area is a deep hub-and-spoke tree (Organizer home → a few
/// section hubs → ~50 feature pages) reached via a single collapsed dropdown, so
/// a deep feature page otherwise gives no "where am I / one click back to the
/// parent hub" affordance. This builder is the single source of truth for that
/// trail; it reuses the SAME hub→section grouping the nav uses (NavBuilder) and
/// an explicit, unit-tested feature-page→hub map, so a route is never guessed.
///
/// Routes are never renamed here — this is read-only navigation metadata.
/// Unknown / non-organizer paths return an empty trail (the view renders nothing),
/// so it is safe to call on every page.
/// </summary>
public static class BreadcrumbBuilder
{
    /// <summary>The organizer-area root the whole tree hangs under.</summary>
    private const string RootHref = "/Organizer";
    private const string RootLabelKey = "Nav.OrgArea";

    /// <summary>
    /// The section-hub landing pages (the spokes off the organizer root). Each is
    /// a direct child of the root, so its trail is just the root. Keyed by the
    /// normalized hub route; the value is the hub's own label key (reused by the
    /// feature-page map below as the parent crumb's label).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Hubs =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/organizer/people"]       = "Nav.OrgPeople",
            ["/organizer/content"]      = "Nav.OrgSessionsHub",
            ["/organizer/comms"]        = "Nav.OrgComms",
            ["/organizer/some"]         = "Nav.OrgSoMe",
            ["/organizer/volunteers"]   = "Nav.OrgVolunteers",
            ["/organizer/logistics"]    = "Nav.OrgLogistics",
            ["/organizer/setup"]        = "Nav.OrgSetup",
        };

    /// <summary>
    /// Feature page → its parent hub route. The trail of a feature page is
    /// <c>root ▸ hub</c>. Reuses the same hub grouping the organizer hub landing
    /// pages link out to (People / Sessions(Content) / Comms / SoMe / Volunteers /
    /// Logistics / Setup), so the breadcrumb agrees with the nav. Routes are the
    /// normalized (lower-case, no trailing slash) request path.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FeatureToHub =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // People hub
            ["/organizer/participants"]          = "/organizer/people",
            ["/organizer/editparticipant"]       = "/organizer/people",
            ["/organizer/editonbehalf"]          = "/organizer/people",
            ["/organizer/preselectionqueue"]     = "/organizer/people",
            ["/organizer/onboarding"]            = "/organizer/people",
            ["/organizer/attendees"]             = "/organizer/people",
            ["/organizer/actionqueue"]           = "/organizer/people",
            ["/organizer/findperson"]            = "/organizer/people",
            ["/organizer/testdatacleanup"]       = "/organizer/people",

            // Sessions & speakers hub (route is /Organizer/Content)
            ["/organizer/speakers"]              = "/organizer/content",
            ["/organizer/sessions"]              = "/organizer/content",
            ["/organizer/sessionquestions"]      = "/organizer/content",
            ["/organizer/sessionevaluations"]    = "/organizer/content",
            ["/organizer/sessionizeimport"]      = "/organizer/content",
            ["/organizer/sessionizeendpointsettings"] = "/organizer/content",
            ["/organizer/speakerreminders"]      = "/organizer/content",

            // Comms hub
            ["/organizer/emailcenter"]           = "/organizer/comms",
            ["/organizer/emaillog"]              = "/organizer/comms",
            ["/organizer/broadcast"]             = "/organizer/comms",
            ["/organizer/sendinvitations"]       = "/organizer/comms",
            ["/organizer/sendwelcomelogin"]      = "/organizer/comms",

            // Marketing / SoMe hub
            ["/organizer/graphics"]              = "/organizer/some",
            ["/organizer/assetlocations"]        = "/organizer/some",
            ["/organizer/groupphotos"]           = "/organizer/some",

            // Volunteers hub
            ["/organizer/volunteerstructure"]    = "/organizer/volunteers",
            ["/organizer/bucketallocation"]      = "/organizer/volunteers",

            // Logistics hub
            ["/organizer/hotels"]                = "/organizer/logistics",
            ["/organizer/hotelassignments"]      = "/organizer/logistics",
            ["/organizer/hotelroomblocks"]       = "/organizer/logistics",
            ["/organizer/swag"]                  = "/organizer/logistics",
            ["/organizer/lunch"]                 = "/organizer/logistics",
            ["/organizer/travelreimbursements"]  = "/organizer/logistics",
            ["/organizer/exports"]               = "/organizer/logistics",
            ["/organizer/datafreshness"]         = "/organizer/logistics",
            ["/organizer/impersonationlog"]      = "/organizer/logistics",

            // Setup hub
            ["/organizer/calendarsettings"]      = "/organizer/setup",
            ["/organizer/settings"]              = "/organizer/setup",
        };

    /// <summary>
    /// The ancestor trail for <paramref name="requestPath"/>, root-first, NOT
    /// including the current page (the view appends the page title as the leaf).
    /// Returns an empty list for the organizer root itself and for any path that
    /// is not a recognised organizer-area page, so it is safe to call everywhere.
    /// </summary>
    public static IReadOnlyList<BreadcrumbCrumb> Build(string? requestPath)
    {
        var path = Normalize(requestPath);
        if (path is null || !path.StartsWith("/organizer", StringComparison.Ordinal))
        {
            return Array.Empty<BreadcrumbCrumb>();
        }

        // The organizer root itself has no ancestors (it IS the root).
        if (path == "/organizer")
        {
            return Array.Empty<BreadcrumbCrumb>();
        }

        // A section hub page: its only ancestor is the root.
        if (Hubs.ContainsKey(path))
        {
            return new[] { new BreadcrumbCrumb(RootHref, RootLabelKey) };
        }

        // A feature page: root ▸ its hub.
        if (FeatureToHub.TryGetValue(path, out var hubPath)
            && Hubs.TryGetValue(hubPath, out var hubLabelKey))
        {
            return new[]
            {
                new BreadcrumbCrumb(RootHref, RootLabelKey),
                new BreadcrumbCrumb(CanonicalHref(hubPath), hubLabelKey),
            };
        }

        // A known organizer page not in the map (prominent entries like
        // CommandCenter/Dashboard, or a not-yet-mapped page): show just the root
        // so there is always a one-click way back, without guessing a section.
        return new[] { new BreadcrumbCrumb(RootHref, RootLabelKey) };
    }

    /// <summary>
    /// Lower-cases + trims a trailing slash so map lookups are case/slash
    /// insensitive; returns null for null/empty/whitespace.
    /// </summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var p = path.Trim().ToLowerInvariant();
        if (p.Length > 1)
        {
            p = p.TrimEnd('/');
        }

        return p.Length == 0 ? "/" : p;
    }

    /// <summary>
    /// Restores a canonical PascalCase href for the hub link from the normalized
    /// key, so the rendered link matches the real route casing. Only the known
    /// hub routes are produced here.
    /// </summary>
    private static string CanonicalHref(string normalizedHubPath) => normalizedHubPath switch
    {
        "/organizer/people"     => "/Organizer/People",
        "/organizer/content"    => "/Organizer/Content",
        "/organizer/comms"      => "/Organizer/Comms",
        "/organizer/some"       => "/Organizer/SoMe",
        "/organizer/volunteers" => "/Organizer/Volunteers",
        "/organizer/logistics"  => "/Organizer/Logistics",
        "/organizer/setup"      => "/Organizer/Setup",
        _ => normalizedHubPath,
    };
}
