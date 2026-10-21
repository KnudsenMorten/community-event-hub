using System.Text.RegularExpressions;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// §1305 — reads the booth and the timeslot a product sells out of its NAME.
/// </summary>
/// <remarks>
/// <para>🔑 <b>The webshop has no field for either</b> (measured on the live shop 2026-09-26: no SKU,
/// no attributes, no usable meta). The name is the only place the booth and the slot exist:
/// <c>Gold Exhibitor ELDK27 - Booth E-20A (Shared)</c>,
/// <c>Sponsor Long 60 min Session - ELDK27 - timeslot: 2 (est. time: 11:30-12:30)</c>,
/// <c>Sponsor Morning 50 min Session - ELDK27 - timeslot: early (est. time: 07:20-08:10)</c>.</para>
///
/// <para>⚠️ A name that does not match yields null, never a guess: a booth id invented from a
/// near-miss would put a sponsor on the wrong spot of the expo map.</para>
/// </remarks>
public static class ShopProductNameParser
{
    // "Booth E-1", "Booth E-20A", "Booth E-24 (feature size)". The id is E-<digits><optional letter>.
    private static readonly Regex BoothRx = new(
        @"\bBooth\s+(?<id>E-\d{1,3}[A-Z]?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // "timeslot: 2", "timeslot: early", optionally followed by "(est. time: 11:30-12:30)".
    private static readonly Regex SlotRx = new(
        @"\btimeslot:\s*(?<slot>[A-Za-z0-9]+)(?:\s*\(est\.?\s*time:\s*(?<time>\d{1,2}:\d{2}\s*-\s*\d{1,2}:\d{2})\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The booth id ("E-20A"), upper-cased, or null.</summary>
    public static string? BoothId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var m = BoothRx.Match(name);
        return m.Success ? m.Groups["id"].Value.ToUpperInvariant() : null;
    }

    /// <summary>The timeslot label ("2", "early") and its stated time ("11:30-12:30"), or nulls.</summary>
    public static (string? Slot, string? Time) Timeslot(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, null);
        var m = SlotRx.Match(name);
        if (!m.Success) return (null, null);
        var time = m.Groups["time"].Success
            ? Regex.Replace(m.Groups["time"].Value, @"\s+", string.Empty)
            : null;
        return (m.Groups["slot"].Value.ToLowerInvariant(), time);
    }
}

/// <summary>
/// §1305 — the default sponsor-facing <see cref="ShopGroup"/> for a webshop category, by its name.
/// </summary>
/// <remarks>
/// <para>Derived from the live shop's thirty categories (2026-09-26). Used only as the DEFAULT: an
/// organizer's choice (<see cref="ShopCategory.GroupSetByOrganizer"/>) always wins, so a new or renamed
/// category can be placed without a deploy.</para>
///
/// <para>🔑 Matched on words, not exact names, because the shop names carry edition noise and
/// ampersands. A category that matches nothing stays <see cref="ShopGroup.Unassigned"/> and inherits
/// its parent's group in <see cref="Resolve"/> — never silently dumped into a wrong door.</para>
/// </remarks>
public static class ShopGroupMapper
{
    /// <summary>The group a category name implies on its own, or Unassigned.</summary>
    public static ShopGroup FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ShopGroup.Unassigned;
        var n = name.ToLowerInvariant();

        // Order matters: the most specific signal first.
        if (n.Contains("package handling") || n.Contains("customs") || n.Contains("freight"))
            return ShopGroup.PackageHandling;
        // "Swag Sponsor" is the free-swag sponsorship the organizer assigns by hand — never a product a sponsor picks.
        if (n.Contains("swag sponsor")) return ShopGroup.OrganizerOnly;
        // Operator 2026-09-26: "hide founding partner sponsor option" — only an organizer can offer it.
        if (n.Contains("founding partner") || n.Contains("found partner")) return ShopGroup.OrganizerOnly;
        if (n.Contains("swag")) return ShopGroup.Swag;
        // ⚠️ Before "ticket": "Extra Staff Tickets" is a booth option, not a ticket pack.
        // Furniture and the other booth options are two doors (operator 2026-09-26) — the tier's
        // included-furniture allowance applies to furniture only.
        if (n.Contains("furniture")) return ShopGroup.Furniture;
        if (n.Contains("booth option") || n.Contains("tv rental")
            || n.Contains("bag packaging") || n.Contains("staff ticket"))
            return ShopGroup.BoothOptions;
        if (n.Contains("ticket")) return ShopGroup.Tickets;
        if (n.Contains("session") || n.Contains("track sponsor")) return ShopGroup.Sessions;
        if (n.Contains("exhibitor")) return ShopGroup.Booths;
        if (n.Contains("digital only") || n.Contains("silver"))
            return ShopGroup.Packages;
        if (n.Contains("branded feature") || n.Contains("hospitality") || n.Contains("content & program")
            || n.Contains("appreciation") || n.Contains("attendee experience"))
            return ShopGroup.Branding;

        return ShopGroup.Unassigned;
    }

    /// <summary>
    /// A category's group: its own name first, else the nearest ancestor that has one.
    /// </summary>
    /// <param name="nameById">Category names by webshop id.</param>
    /// <param name="parentById">Parent webshop id by webshop id (0 / absent = top level).</param>
    public static ShopGroup Resolve(
        long wooCategoryId,
        IReadOnlyDictionary<long, string> nameById,
        IReadOnlyDictionary<long, long> parentById)
    {
        var seen = new HashSet<long>();
        var id = wooCategoryId;
        while (id > 0 && seen.Add(id))
        {
            if (nameById.TryGetValue(id, out var name))
            {
                var g = FromName(name);
                if (g != ShopGroup.Unassigned) return g;
            }
            if (!parentById.TryGetValue(id, out var parent)) break;
            id = parent;
        }
        return ShopGroup.Unassigned;
    }
}
