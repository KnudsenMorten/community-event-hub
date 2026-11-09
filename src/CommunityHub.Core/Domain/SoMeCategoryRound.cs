namespace CommunityHub.Core.Domain;

/// <summary>
/// §1195 — ONE ROUND OF ONE CATEGORY, with the day it starts.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this exists, when §1187 already had a start and an end.</b> Spreading <i>n</i>
/// rounds evenly between two dates is a tidy model and it <b>silently discards the dates he actually
/// gave</b>: tracks at 28 Sep / 1 Dec / 15 Jan become 28 Sep / ~14 Nov / ~30 Dec — and the third
/// lands inside the Christmas blackout, so it moves again. He asked for the simpler model AND named
/// specific rounds, and the first reading of that lost the second half.</para>
///
/// <para>🔑 So a round carries its own date. <see cref="StartsOn"/> null means "no opinion" — that
/// round spreads between the round before it and the category's end, which is the even-spread model
/// for anyone who does not want to name every date. Naming them all reproduces exactly what he
/// dictated; naming none behaves like §1187's spread.</para>
///
/// <para>🔒 This is also what "add a round" means: a new row. The count on
/// <see cref="SoMeCategoryRule.Rounds"/> stays the authority on HOW MANY (so a category can gain a
/// round without anyone picking a date for it), and these rows say WHEN for the ones he has an
/// opinion about.</para>
/// </remarks>
public class SoMeCategoryRound
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public SoMeAnnouncementCategory Category { get; set; }

    /// <summary>1-based. Round 1 is the announcement; later rounds are reminders.</summary>
    public int RoundNumber { get; set; }

    /// <summary>
    /// The day this round starts. Null = spread it between the previous round and the category's end.
    /// </summary>
    /// <remarks>
    /// ⚠️ A round never opens before the one before it, whatever is typed here — a reminder dated
    /// ahead of its own announcement is worse than one with no date at all.
    /// </remarks>
    public DateOnly? StartsOn { get; set; }

    /// <summary>
    /// §1368 — the last day this round may be PUBLISHED on. Null = no deadline for this round.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-10-26: <i>"all sponsors first post must be no later than dec 15 on
    /// linkedin"</i> — Sponsors, round 1, 15 December 2026.</para>
    /// <para>🔑 A DEADLINE, which is not the same thing as <see cref="SoMeCategoryRule.EndsOn"/>: the
    /// category end caps EVERY round, and a sponsor's reminder round must still be allowed to run in
    /// January. This caps one round only.</para>
    /// <para>⚠️ It cannot be met for a sponsor who signs after the date, and that case is expected
    /// (<i>"this will happen if we havent sold all sponsorship by dec 15, then i will move the
    /// dates"</i>). The post is then placed as soon as possible and the conflict is MAILED — never
    /// dropped (§842.5).</para>
    /// </remarks>
    public DateOnly? PublishBy { get; set; }
}
