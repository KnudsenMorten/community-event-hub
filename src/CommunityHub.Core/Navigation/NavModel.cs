using System.Collections.Generic;
using System.Linq;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Navigation;

/// <summary>
/// One navigation entry. <see cref="LabelKey"/> is a SharedResource resx key
/// (NOT a localized string) so the view layer owns i18n via <c>@Localizer</c>;
/// when a route has no resx key yet, <see cref="FallbackLabel"/> carries the
/// literal text and <see cref="LabelKey"/> is null. <see cref="Href"/> is the
/// route path; <see cref="ExactMatch"/> drives aria-current matching ("/" must
/// only be current for the exact root).
///
/// <see cref="SectionKey"/> is an OPTIONAL resx key naming the collapsible
/// sub-group this item belongs to inside a menu (REQUIREMENTS §21 "Group the
/// organizer nav" — People / Sessions / Comms / Sponsors / Volunteers /
/// Logistics). It is null for prominent, ungrouped items (Organizer home,
/// Command center, Dashboard) that render directly at the top of the menu. It is
/// pure information-architecture metadata — it never changes the route, the
/// gating, or the membership of the menu.
/// </summary>
/// <param name="External">
/// True for a link to a DIFFERENT site (e.g. the Zoho exhibitor dashboard or the
/// webshop). The view renders it with <c>target="_blank" rel="noopener noreferrer"</c>
/// so it opens in a new tab and the hub stays open behind it. Internal hub routes
/// leave this false (same-tab navigation).
/// </param>
/// <param name="FeatureKey">
/// Optional FeatureCatalog key this menu item represents. When set, the view
/// resolves the feature's released ring + the user's effective ring to (a) GATE
/// the item (hide it from users above the ring) and (b) BADGE it with a yellow
/// "Ring N" pill while it is not yet Broad/GA — so a ring tester sees exactly what
/// is scoped to them to test. Null for ungated, always-visible items.
/// </param>
/// <param name="Redirect">
/// When not <see cref="ExternalRedirectKind.None"/>, the link routes the user OUT of
/// the hub into a third-party system that needs its OWN login (the Zoho exhibitor
/// dashboard §175, the sponsor webshop §176). The view renders a <c>data-confirm</c>
/// attribute carrying the kind's localized "you're leaving for X, here's how to log in"
/// message, and a small shared JS handler shows a <c>confirm()</c> before navigating
/// (OK proceeds as today, Cancel stays). Pure UX metadata — it never changes the route
/// or the gating.
/// </param>
// §297: SubSectionKey is an OPTIONAL resx key naming a NESTED sub-fold-out inside this item's
// SectionKey section (two-level menu) — e.g. "Policies" (Code of Conduct + Privacy Policy) nested
// inside "Event Info". Null = a direct leaf of the section.
public sealed record NavItem(string Href, string? LabelKey, string? FallbackLabel = null, bool ExactMatch = false, string? SectionKey = null, bool External = false, string? FeatureKey = null, ExternalRedirectKind Redirect = ExternalRedirectKind.None, string? SubSectionKey = null);

/// <summary>
/// Which third-party system a nav link redirects to, for the "you'll need to log in
/// there" confirm popup (REQUIREMENTS §175 Zoho, §176 sponsor webshop). The view maps
/// each non-<see cref="None"/> kind to its localized message and renders it as a
/// <c>data-confirm</c> attribute; <see cref="None"/> links navigate with no prompt.
/// </summary>
public enum ExternalRedirectKind
{
    /// <summary>Internal hub link (or a plain external link) — navigate with no confirm.</summary>
    None = 0,

    /// <summary>The Zoho exhibitor dashboard — email login + a one-time password (§175).</summary>
    Zoho = 1,

    /// <summary>The sponsor webshop — email + password login, with a "lost your password" link (§176).</summary>
    Webshop = 2,
}

/// <summary>
/// A named, collapsible sub-group of nav items within a single <see cref="NavGroup"/>
/// (REQUIREMENTS §21 organizer-nav grouping). <see cref="HeadingKey"/> is the resx
/// key for the group's disclosure label (null for the leading "prominent" bucket of
/// ungrouped items that render without a heading). The view renders each section as a
/// native <c>&lt;details&gt;/&lt;summary&gt;</c> disclosure (no JS, keyboard-accessible).
/// </summary>
public sealed record NavSection(string? HeadingKey, IReadOnlyList<NavItem> Items);

/// <summary>
/// A named, role-gated section of the nav. The participant ("My event") group is
/// always visible to a signed-in participant; the organizer ("Organizer area")
/// group is emitted by <see cref="NavBuilder"/> ONLY when the role is Organizer
/// (server-side gate — a non-organizer never receives the items at all, not a
/// CSS hide). <see cref="IsManagement"/> tags the organizer group so the view
/// can render it as a collapsible dropdown and tests can assert its contents.
/// </summary>
public sealed record NavGroup(string? HeadingKey, IReadOnlyList<NavItem> Items, bool IsManagement = false)
{
    /// <summary>
    /// The items bucketed into ordered, named sub-groups by <see cref="NavItem.SectionKey"/>
    /// (REQUIREMENTS §21 organizer-nav grouping). Order is preserved: sections appear in the
    /// order their first item appears, items keep their order within a section. Ungrouped items
    /// (null <see cref="NavItem.SectionKey"/>) collapse into a single leading section with a null
    /// heading. The flat <see cref="Items"/> list is unchanged — this is a view-only projection.
    /// </summary>
    public IReadOnlyList<NavSection> Sections()
    {
        // A null SectionKey (the prominent, ungrouped bucket) maps to this sentinel
        // so the dictionary key stays non-null; it is mapped back to null on output.
        const string NullSection = "\0__none__";
        var order = new List<string>();
        var buckets = new Dictionary<string, List<NavItem>>();
        foreach (var item in Items)
        {
            var key = item.SectionKey ?? NullSection;
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<NavItem>();
                buckets[key] = list;
                order.Add(key);
            }
            list.Add(item);
        }
        return order
            .Select(key => new NavSection(key == NullSection ? null : key, buckets[key]))
            .ToList();
    }

    /// <summary>
    /// Like <see cref="Sections()"/>, but the leaves inside the section(s) named in
    /// <paramref name="alphabetizeSectionKeys"/> are ordered by their RESOLVED, localized
    /// display label (ascending, case-insensitive) rather than insertion order
    /// (REQUIREMENTS §173d — the shared "Event logistics" fold-out gathers leaves from
    /// several code paths in mixed order and should read A→Z to the user).
    /// <paramref name="resolveLabel"/> turns a <see cref="NavItem"/> into the label the
    /// user actually SEES (the view passes the localizer; a test passes a stub), so the
    /// sort is by the rendered text, never the resx key. Every other section keeps its
    /// insertion order, and the section grouping/order is unchanged — pure view projection.
    /// </summary>
    public IReadOnlyList<NavSection> Sections(
        Func<NavItem, string?> resolveLabel, params string[] alphabetizeSectionKeys)
    {
        var sections = Sections();
        if (resolveLabel is null || alphabetizeSectionKeys is null || alphabetizeSectionKeys.Length == 0)
            return sections;

        var sortKeys = new HashSet<string>(alphabetizeSectionKeys);
        return sections
            .Select(s => s.HeadingKey is not null && sortKeys.Contains(s.HeadingKey)
                ? new NavSection(s.HeadingKey,
                    s.Items
                        .OrderBy(i => resolveLabel(i) ?? string.Empty,
                                 System.StringComparer.CurrentCultureIgnoreCase)
                        .ToList())
                : s)
            .ToList();
    }
}

/// <summary>
/// The full nav for one signed-in participant: the always-visible participant
/// group plus, for organizers only, the management group.
/// </summary>
public sealed record NavModel(IReadOnlyList<NavGroup> Groups)
{
    /// <summary>Every nav item across every group (flattened) — convenience for tests/markup.</summary>
    public IEnumerable<NavItem> AllItems => Groups.SelectMany(g => g.Items);

    /// <summary>The single management group, or null when the role is not an organizer.</summary>
    public NavGroup? ManagementGroup => Groups.FirstOrDefault(g => g.IsManagement);

    /// <summary>True when this nav exposes the organizer/management group.</summary>
    public bool HasManagement => ManagementGroup is not null;
}
