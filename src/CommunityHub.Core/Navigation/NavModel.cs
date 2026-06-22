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
public sealed record NavItem(string Href, string? LabelKey, string? FallbackLabel = null, bool ExactMatch = false, string? SectionKey = null, bool External = false, string? FeatureKey = null);

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
