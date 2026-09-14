namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1179 — NOBODY IS READING LINKEDIN BETWEEN CHRISTMAS AND NEW YEAR.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-12, on an auto-approval mail naming <i>"Wed 30 Dec 14:30"</i> and
/// <i>"Fri 01 Jan 11:00"</i>: <i>"dates are all over the place, even dec 30 and jan 1"</i> ·
/// <i>"this is not good"</i> · <i>"lets keep the current design, but make blockout between dec 23 -
/// jan 3 due to holidays"</i>.</para>
///
/// <para>🔑 <b>The spread was not malfunctioning — it had no idea holidays exist.</b>
/// <c>SoMeSchedulePlanner</c>'s ONLY calendar rule was <c>IsWeekend</c>, at both of its placement
/// sites. 30 Dec is a Wednesday and 1 Jan a Friday, so §848.3's even spread — which he asked for on
/// 2026-08-05 and whose own measured table shows <b>Dec 32 / Jan 31</b> posts — placed them there
/// exactly as designed. ⚠️ So this is a GAP, not a decision to reverse: §848.1's whole-period spread
/// stays, and it simply gains a calendar it did not have.</para>
///
/// <para>🔒 <b>Recurring by month and day, never a fixed year.</b> CEH is evergreen — a new edition is
/// a new <c>Event</c> row and a JSON config (CLAUDE.md), so a blackout pinned to 2026/2027 would
/// silently stop protecting the next edition and nobody would notice until a post went out on
/// Christmas Eve. Expressed as month/day, every edition inherits it with nothing to set.</para>
///
/// <para>⚠️ <b>Deliberately NOT configurable yet, and that is a scope choice worth stating.</b> He
/// asked for one range, so this ships as one rule rather than a settings column, a migration, a
/// parser and a page field. If he wants to widen it per edition, the natural home is a
/// <c>SoMeSettings</c> field feeding <see cref="IsBlackedOut"/> — a small addition, and none of the
/// callers change.</para>
/// </remarks>
public static class SoMeBlackout
{
    /// <summary>First blacked-out day of the year-end holiday, as (month, day).</summary>
    public const int StartMonth = 12;
    public const int StartDay = 23;

    /// <summary>Last blacked-out day, in the FOLLOWING year.</summary>
    public const int EndMonth = 1;
    public const int EndDay = 3;

    /// <summary>Words for the log and the settings page, so the rule is never a bare date test.</summary>
    public const string Description = "23 December – 3 January (holidays)";

    /// <summary>
    /// True when nothing may be announced on this day.
    /// </summary>
    /// <remarks>
    /// 🔑 The range WRAPS THE YEAR, so it cannot be written as one comparison: 23–31 December belongs
    /// to the old year and 1–3 January to the new one. Both ends are INCLUSIVE — he named the first
    /// and last day people are away, not a half-open interval.
    /// </remarks>
    public static bool IsBlackedOut(DateOnly day) =>
        (day.Month == StartMonth && day.Day >= StartDay)
        || (day.Month == EndMonth && day.Day <= EndDay);

    /// <summary>
    /// The first day on or after <paramref name="day"/> that is not blacked out.
    /// </summary>
    /// <remarks>
    /// ⚠️ Weekends are NOT considered here — the planner's own loops already skip those, and
    /// duplicating that rule in a second place is how two calendars start disagreeing. This answers
    /// one question only: when does the holiday end.
    /// </remarks>
    public static DateOnly NextAllowedDay(DateOnly day)
    {
        // At most 12 steps (23 Dec → 4 Jan); a loop is clearer than the two-case arithmetic and
        // cannot be wrong about a leap year or a year boundary.
        while (IsBlackedOut(day)) day = day.AddDays(1);
        return day;
    }
}
