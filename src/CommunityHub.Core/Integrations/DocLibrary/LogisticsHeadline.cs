namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §6.3 — how a logistics file states its one headline figure.
/// </summary>
/// <remarks>
/// <para>The work order asks each artifact on the organizer page to show a <i>"row count or headline
/// figure"</i>. This is the one place that phrases it, so eleven files do not each invent their own
/// wording for the same idea.</para>
///
/// <para>🔒 <b>An ESTIMATE is labelled as one, always.</b> Breakfast has no sign-up and is derived
/// from a stated formula (§770.8); the venue is billed against it. A figure that cannot tell the
/// organizer whether it was counted or calculated invites him to treat a projection as a register,
/// which is exactly the mistake that costs money in both directions.</para>
///
/// <para>⚠️ <b>Zero is stated, never blanked.</b> "0 people" and "" are different facts — the second
/// reads as "not generated yet", and a file that is genuinely empty is something somebody needs to
/// see rather than something to hide.</para>
/// </remarks>
public static class LogisticsHeadline
{
    /// <summary>The most common shape: a count of people.</summary>
    public static string People(int count) => Of(count, "person", "people");

    /// <summary>A count with its own noun, pluralised.</summary>
    public static string Of(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";

    /// <summary>
    /// A figure that was CALCULATED rather than counted — marked so it can never be read as a
    /// register of named people.
    /// </summary>
    public static string Estimate(int count, string singular, string plural) =>
        $"≈{Of(count, singular, plural)} (estimate)";
}
