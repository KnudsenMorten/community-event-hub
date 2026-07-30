using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Content;

/// <summary>
/// One generic, operator-authored CONTENT-HUB page (REQUIREMENTS §104–§123).
/// The actual prose/images live in a Markdown file under
/// <c>config/content/&lt;edition&gt;/{Slug}.md</c> (rendered by the
/// <c>/Info/{slug}</c> page); this record carries only the metadata the app
/// needs around it: the display <see cref="Title"/>, the <see cref="MenuSection"/>
/// the nav groups it under, and the <see cref="Roles"/> that may see it.
/// </summary>
/// <param name="Slug">URL + file-name key (e.g. <c>wayfinding</c>).</param>
/// <param name="Title">Human title shown as the page heading + nav label.</param>
/// <param name="MenuSection">Nav fold-out the item lives under (e.g. "Event Logistics").</param>
/// <param name="Roles">
/// Roles allowed to view the page. An EMPTY set means ALL roles (per §123
/// "default to all-roles"); a non-empty set restricts to those roles.
/// <see cref="ParticipantRole.Organizer"/> can always view every page
/// (organizers "see everything"), so it need not be listed.
/// </param>
public sealed record ContentPage(
    string Slug,
    string Title,
    string MenuSection,
    IReadOnlyList<ParticipantRole> Roles,
    string? SpeakerTitle = null)
{
    /// <summary>
    /// §326br — the menu label / page heading for one role. An all-roles page may carry a
    /// SPEAKER-specific title when the page holds a section only speakers act on (the
    /// addresses page also lists the speaker hotel). Every other role keeps
    /// <see cref="Title"/>, so nobody is shown a heading about a benefit they do not get.
    /// </summary>
    public string TitleFor(ParticipantRole role) =>
        role == ParticipantRole.Speaker && !string.IsNullOrWhiteSpace(SpeakerTitle)
            ? SpeakerTitle!
            : Title;
}

/// <summary>
/// The single source of truth that maps a content-page <c>slug</c> to its
/// <see cref="ContentPage"/> metadata + §123 role-scoping. Used by BOTH the
/// generic <c>/Info/{slug}</c> page (title + role-gate) and the nav (which
/// items to show per role — P2). Kept in Core so it is pure + unit-testable
/// with no web dependency.
/// </summary>
public static class ContentPageRegistry
{
    private const string EventLogistics = "Event Logistics";

    // Reusable audience sets.
    private static readonly IReadOnlyList<ParticipantRole> AllRoles =
        Array.Empty<ParticipantRole>();
    private static readonly IReadOnlyList<ParticipantRole> SpeakersOnly =
        new[] { ParticipantRole.Speaker };

    private static readonly IReadOnlyDictionary<string, ContentPage> Pages =
        new[]
        {
            // ALL roles (§123). §317 (operator 2026-07-24): titles + DECLARATION ORDER are
            // the Event Info menu order (the nav renders in insertion order now — the §173d
            // alphabetize is retired): Check Out Last Event → Good To Know → Address →
            // Wayfinding.
            new ContentPage("last-event-videos", "Check Out Last Event - ELDK26", EventLogistics, AllRoles),
            new ContentPage("good-to-know", "Good To Know Before Event", EventLogistics, AllRoles),
            // §326br (operator 2026-07-25): speakers see the speaker hotel on this page, so
            // for them the label names it. Other roles keep the plain venue title.
            new ContentPage("addresses", "Address To Conference Venue", EventLogistics, AllRoles,
                SpeakerTitle: "Address To Conference Venue & Speaker Hotel"),
            new ContentPage("wayfinding", "Wayfinding Inside Conference Venue", EventLogistics, AllRoles),
            // §326ag (operator 2026-07-25): what CEH is, how it is built, and the feature
            // list per role — all roles, last in the Event Info order.
            new ContentPage("ceh-introduction", "Introduction to Community Event Hub solution", EventLogistics, AllRoles),

            // SPEAKERS (+ organizers always) (§123). §317: speaker-only pages are placed
            // EXPLICITLY into the Speaker Info sub-fold-outs by NavBuilder (not via the
            // generic loop) — titles per the operator's menu wording.
            // §326d (operator 2026-07-25): menu item renamed "Speaker template" →
            // "Download Speaker Template" (heading follows per the §318d align rule).
            new ContentPage("speaker-template", "Download Speaker Template", EventLogistics, SpeakersOnly),
            // §326e (operator 2026-07-25): speaker key dates & times — a DIRECT Speaker
            // Info leaf (NavBuilder places it explicitly, before the sub-fold-outs).
            new ContentPage("key-dates-times", "Key Dates & Times", EventLogistics, SpeakersOnly),
            new ContentPage("session-guidelines", "Session Guidelines", EventLogistics, SpeakersOnly),
            new ContentPage("av-stage-timer", "A/V, Comfort Screen, HDMI Switches, Stage-timer", EventLogistics, SpeakersOnly),
            new ContentPage("session-preview-final", "Deadlines: Session Preview / Final guidelines", EventLogistics, SpeakersOnly),
            // §161 (operator 2026-06-28): one entry — "Session feedback" page kept but relabelled
            // "Session Evaluations"; the duplicate "session-evaluations" nav entry dropped.
            new ContentPage("session-feedback", "How We Do Session Evaluations?", EventLogistics, SpeakersOnly),
            // §289 (operator 2026-07-10): the "Social Media Guidelines" nav page was REMOVED — its
            // awareness/tags copy now lives directly on the Help Promote page (/Speaker/Graphics).
        }
        .ToDictionary(p => p.Slug, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every registered content page, in declaration order.</summary>
    public static IReadOnlyCollection<ContentPage> All => Pages.Values.ToList();

    /// <summary>
    /// Look up a page by slug (case-insensitive). Returns null for an unknown
    /// slug so the page can 404.
    /// </summary>
    public static ContentPage? Get(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        return Pages.TryGetValue(slug.Trim(), out var page) ? page : null;
    }

    /// <summary>True when the slug is registered.</summary>
    public static bool Exists(string? slug) => Get(slug) is not null;

    /// <summary>
    /// §123 role-gate: can <paramref name="role"/> view <paramref name="slug"/>?
    /// Unknown slug =&gt; false. Organizers see everything. An empty role set on
    /// the page means all roles; otherwise the role must be listed.
    /// </summary>
    public static bool CanAccess(string? slug, ParticipantRole role)
    {
        var page = Get(slug);
        if (page is null) return false;
        if (role == ParticipantRole.Organizer) return true;
        return page.Roles.Count == 0 || page.Roles.Contains(role);
    }

    /// <summary>
    /// The content pages visible to <paramref name="role"/> — the seam the nav
    /// (P2) uses to build the per-role Event Logistics fold-out.
    /// </summary>
    public static IReadOnlyList<ContentPage> ForRole(ParticipantRole role) =>
        Pages.Values.Where(p => CanAccess(p.Slug, role)).ToList();
}
