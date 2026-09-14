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
    /// <remarks>
    /// 🔒 §831 — THIS MAP MUST LIST EVERY PAGE A HUB CARD LINKS TO. He reported the breadcrumb as
    /// wrong "on every page": <c>/Organizer/ContentStudio</c> showed <c>Organizer Area / Content
    /// Studio</c>, dropping the Marketing / SoMe hub he had just clicked through. The builder was
    /// never broken — <b>ContentStudio simply was not in this map</b>, so it hit the closing
    /// root-only fallback. 54 of the 95 organizer pages were in that state.
    ///
    /// <para>The authoritative parent is <b>the hub whose landing page carries a card pointing at the
    /// page</b> — that is literally the path he walked, and it is the same grouping the nav uses.
    /// <c>BreadcrumbHubCardCoverageTests</c> (Web.Tests) parses the hub Razor pages and fails if a
    /// card target is missing here, so the next page added to a hub cannot silently degrade to
    /// root-only the way ContentStudio did.</para>
    ///
    /// <para>⚠️ Three pages are carded on TWO hubs. A trail has one parent, so the primary is chosen
    /// explicitly and noted at the entry rather than left to dictionary insertion order.</para>
    ///
    /// <para>Pages with NO hub card (Dashboard, CommandCenter, Overview, …) are deliberately absent:
    /// they are reached from the root, so root-only is their CORRECT trail, not a gap.</para>
    /// </remarks>
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
            ["/organizer/accesslinks"]           = "/organizer/people",
            ["/organizer/welcomelinks"]          = "/organizer/people",
            // §1040 — Volume Package monitors, carded on People beside the two sign-in-link pages.
            ["/organizer/attendeemonitors"]      = "/organizer/people",
            ["/organizer/volumepackage"]         = "/organizer/people",
            ["/organizer/pendingspeakers"]       = "/organizer/people",
            // §1085 — the all-roles status board. Carded on People AND on the Sponsor Admin hub
            // (deep-linked to ?role=Sponsor); primary = People, because "who still owes me
            // something?" is a question about PEOPLE and the sponsor view is one filter of it.
            ["/organizer/participantstatus"]     = "/organizer/people",
            ["/organizer/economiccontacts"]      = "/organizer/people",
            ["/organizer/sponsorwebshopcompany"] = "/organizer/people",

            // Sessions & speakers hub (route is /Organizer/Content)
            ["/organizer/speakers"]              = "/organizer/content",
            ["/organizer/sessions"]              = "/organizer/content",
            ["/organizer/sessionquestions"]      = "/organizer/content",
            ["/organizer/sessionevaluations"]    = "/organizer/content",
            ["/organizer/sessionizeimport"]      = "/organizer/content",
            // Carded on BOTH Content and Setup. Primary = Content: it configures the Sessionize
            // import, which is a content concern; Setup lists it as a convenience.
            ["/organizer/sessionizeendpointsettings"] = "/organizer/content",
            ["/organizer/masterclasses"]         = "/organizer/content",
            ["/organizer/sessionfeedback"]       = "/organizer/content",
            ["/organizer/sessionsource"]         = "/organizer/content",
            ["/organizer/speakerreadiness"]      = "/organizer/content",
            ["/organizer/surveys"]               = "/organizer/content",
            ["/organizer/syncqueue"]             = "/organizer/content",

            // Comms hub
            ["/organizer/emailcenter"]           = "/organizer/comms",
            ["/organizer/emaillog"]              = "/organizer/comms",
            ["/organizer/broadcast"]             = "/organizer/comms",
            ["/organizer/sendinvitations"]       = "/organizer/comms",
            ["/organizer/sendwelcomelogin"]      = "/organizer/comms",
            ["/organizer/emailtemplates"]        = "/organizer/comms",
            ["/organizer/feed"]                  = "/organizer/comms",
            // Carded on Comms (not SoMe) — it is the speaker MAIL cadence.
            ["/organizer/speakerreminders"]      = "/organizer/comms",

            // Marketing / SoMe hub
            ["/organizer/graphics"]              = "/organizer/some",
            ["/organizer/designergraphics"]      = "/organizer/some",
            ["/organizer/assetlocations"]        = "/organizer/some",
            // §831 — THE PAGE HE REPORTED. Renamed to "WordPress BlogPost Content" in §832;
            // the ROUTE deliberately stays /Organizer/ContentStudio.
            ["/organizer/contentstudio"]         = "/organizer/some",
            // §828 — the event-post repo (Type 5 copy, imported from his deck).
            ["/organizer/eventposts"]            = "/organizer/some",
            // §833 — the three §824 features that had no home on this hub. Carded on BOTH SoMe and
            // Comms; primary = SoMe, because "where is the post editor" is a SoMe question — being
            // filed only under Comms is why he could not find it.
            ["/organizer/sometemplates"]         = "/organizer/some",
            ["/organizer/somescheduler"]         = "/organizer/some",
            // §1215 — posting capacity, carded on the hub (§1199 had it planner-link only).
            ["/organizer/somecapacity"]          = "/organizer/some",
            // §834 — the setup wizard. §835 — the campaign calendar.
            ["/organizer/somesetup"]             = "/organizer/some",
            ["/organizer/somecalendar"]          = "/organizer/some",
            // §841 — the one-post-at-a-time editor.
            ["/organizer/someposteditor"]        = "/organizer/some",
            // §842.2 — the per-type frequency page.
            ["/organizer/somecadence"]           = "/organizer/some",
            // Was on NO hub at all, while deciding whether CEH can post to LinkedIn.
            ["/organizer/linkedinconnect"]       = "/organizer/some",
            // Carded on BOTH SoMe and Comms. Primary = SoMe: it is the social queue.
            ["/organizer/somequeue"]             = "/organizer/some",
            // Carded on BOTH SoMe and Setup. Primary = SoMe: these are the SoMe channel settings.
            ["/organizer/somesettings"]          = "/organizer/some",

            // Volunteers hub
            // §1146e — the availability overview, moved off the Volunteers hub onto its own page.
            ["/organizer/volunteeravailability"] = "/organizer/volunteers",
            ["/organizer/volunteerstructure"]    = "/organizer/volunteers",
            ["/organizer/bucketallocation"]      = "/organizer/volunteers",
            ["/organizer/allocationscenarios"]   = "/organizer/volunteers",
            ["/organizer/editvolunteertask"]     = "/organizer/volunteers",
            ["/organizer/organizerallocation"]   = "/organizer/volunteers",
            ["/organizer/volunteertasksexcel"]   = "/organizer/volunteers",

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
            // Carded on Logistics (not SoMe) — the group PHOTO shoot is a schedule/logistics job.
            ["/organizer/groupphotos"]           = "/organizer/logistics",
            ["/organizer/partyrsvps"]            = "/organizer/logistics",
            ["/organizer/schedule"]              = "/organizer/logistics",
            ["/organizer/sessionevalsqr"]        = "/organizer/logistics",
            ["/organizer/speakerreview"]         = "/organizer/logistics",
            ["/organizer/synchealth"]            = "/organizer/logistics",

            // Setup hub
            ["/organizer/calendarsettings"]      = "/organizer/setup",
            ["/organizer/settings"]              = "/organizer/setup",
            ["/organizer/audittrail"]            = "/organizer/setup",
            ["/organizer/couponinvoicing"]       = "/organizer/setup",
            ["/organizer/doclibrarypaths"]       = "/organizer/setup",
            ["/organizer/evaluationapiclients"]  = "/organizer/setup",
            ["/organizer/evaluationdevices"]     = "/organizer/setup",
            ["/organizer/jobs"]                  = "/organizer/setup",
            ["/organizer/platformhealth"]        = "/organizer/setup",
            // Carded on Setup (moved from People, which had no card for it).
            ["/organizer/testdatacleanup"]       = "/organizer/setup",
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
