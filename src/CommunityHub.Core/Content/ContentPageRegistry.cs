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
    IReadOnlyList<ParticipantRole> Roles);

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
            // ALL roles (§123).
            new ContentPage("wayfinding", "Wayfinding – conference venue", EventLogistics, AllRoles),
            new ContentPage("good-to-know", "Good to know before event", EventLogistics, AllRoles),
            new ContentPage("addresses", "Addresses", EventLogistics, AllRoles),
            new ContentPage("last-event-videos", "Check out our last event", EventLogistics, AllRoles),

            // SPEAKERS (+ organizers always) (§123).
            new ContentPage("speaker-template", "Speaker template", EventLogistics, SpeakersOnly),
            new ContentPage("session-guidelines", "Session Guidelines", EventLogistics, SpeakersOnly),
            new ContentPage("av-stage-timer", "A/V, Comfort Screen, HDMI Switchers, Stage-timer", EventLogistics, SpeakersOnly),
            new ContentPage("session-preview-final", "Session Preview / Final guidelines", EventLogistics, SpeakersOnly),
            new ContentPage("session-feedback", "Session feedback", EventLogistics, SpeakersOnly),
            new ContentPage("session-evaluations", "Session Evaluations", EventLogistics, SpeakersOnly),
            new ContentPage("help-promote", "Help Promote", EventLogistics, SpeakersOnly),
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
