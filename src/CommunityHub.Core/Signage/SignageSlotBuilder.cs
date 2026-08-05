using CommunityHub.Core.Domain.Signage;

namespace CommunityHub.Core.Signage;

/// <summary>
/// §754 §5–§7 — which activities belong on a screen right now, in what order, split into pages.
/// Pure: the clock and the activity list are inputs, so the whole slot contract is unit-testable.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Slots are FULL HOURS aligned to the clock</b> (08:00–09:00, 09:00–10:00, …), not
/// "the next N activities". "Happening Now" is the current hour; "Next" is the following one. That
/// is what makes the two views answer the question a person standing in a corridor is actually
/// asking — "can I still get into something?" — rather than showing a list that slides continuously.
/// </para>
///
/// <para>🔒 <b>A multi-hour activity appears in EVERY slot it overlaps.</b> A 10:00–12:00 master
/// class is on the wall in the 10:00 slot AND the 11:00 slot. Anything else makes a running session
/// vanish from "Happening Now" while it is still happening, which is the single most misleading
/// thing a schedule screen can do.</para>
/// </remarks>
public static class SignageSlotBuilder
{
    /// <summary>One page of cards, and where it sits in the rotation.</summary>
    public sealed record Page(int Number, int Of, IReadOnlyList<AgendaActivity> Cards);

    /// <summary>Everything one view needs to render: the slot it is showing and its pages.</summary>
    public sealed record Slot(
        DateTimeOffset StartsAt, DateTimeOffset EndsAt, IReadOnlyList<Page> Pages)
    {
        /// <summary>True when the slot has no activities at all (the view renders its empty state).</summary>
        public bool IsEmpty => Pages.Count == 0;

        /// <summary>Every card in the slot, page order — for tests and for the single-page case.</summary>
        public IReadOnlyList<AgendaActivity> AllCards =>
            Pages.SelectMany(p => p.Cards).ToList();
    }

    /// <summary>
    /// The full-hour slot containing <paramref name="at"/>, expressed in the venue's local time so
    /// the boundary lands on the hour a person at the venue sees on their phone.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) HourSlot(
        DateTimeOffset at, TimeZoneInfo zone, int hoursAhead = 0)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var floor = new DateTimeOffset(
            local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset)
            .AddHours(hoursAhead);
        return (floor, floor.AddHours(1));
    }

    /// <summary>
    /// Build one view's slot. <paramref name="hoursAhead"/> is 0 for "Happening Now" and 1 for
    /// "Next".
    /// </summary>
    public static Slot Build(
        IEnumerable<AgendaActivity> activities,
        DateTimeOffset at,
        TimeZoneInfo zone,
        int pageSize,
        int hoursAhead = 0)
    {
        var (start, end) = HourSlot(at, zone, hoursAhead);

        // 🔒 A HALF-OPEN overlap test, and the ends matter. `StartsAt < end` (not <=) keeps an
        // activity that begins exactly on the next hour out of this slot — it belongs to the next
        // one. `EndsAt > start` (not >=) drops one that ends exactly as the slot opens, because a
        // session that has just finished is not "happening now".
        var inSlot = activities
            .Where(a => a.StartsAt < end && a.EndsAt > start)
            .ToList();

        var ordered = Sort(inSlot);
        var size = Math.Max(1, pageSize);

        var pages = new List<Page>();
        var total = (int)Math.Ceiling(ordered.Count / (double)size);
        for (var i = 0; i < total; i++)
        {
            pages.Add(new Page(i + 1, total, ordered.Skip(i * size).Take(size).ToList()));
        }

        return new Slot(start, end, pages);
    }

    /// <summary>
    /// §6 — sort by TRACK, then ROOM. Deterministic, which is the actual requirement: the page
    /// re-renders every few seconds, and any tie broken by chance would let a card swap pages
    /// between renders and disappear in front of someone reading it.
    /// </summary>
    /// <remarks>
    /// 🔑 The final tiebreak is the Backstage id, not the title — two activities can share a title
    /// (two runs of the same workshop), and a stable id is the only thing guaranteed to be unique.
    /// A blank track or room sorts LAST rather than first: breaks and registration usually carry
    /// neither, and they should not head the wall above the sessions people are looking for.
    /// </remarks>
    public static IReadOnlyList<AgendaActivity> Sort(IEnumerable<AgendaActivity> activities) =>
        activities
            .OrderBy(a => string.IsNullOrWhiteSpace(a.Track) ? 1 : 0)
            .ThenBy(a => a.Track ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => string.IsNullOrWhiteSpace(a.Room) ? 1 : 0)
            .ThenBy(a => a.Room ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.StartsAt)
            .ThenBy(a => a.BackstageSessionId, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Cards per page for an orientation: portrait 2×4 = 8, landscape 5×3 = 15 (§6).
    /// </summary>
    public static int PageSize(SignageSettings s, SignageOrientation orientation) =>
        orientation == SignageOrientation.Portrait
            ? Math.Max(1, s.PortraitColumns) * Math.Max(1, s.PortraitRows)
            : Math.Max(1, s.LandscapeColumns) * Math.Max(1, s.LandscapeRows);
}
