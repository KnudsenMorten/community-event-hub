namespace CommunityHub.Core.Email;

/// <summary>
/// §656 — was this "failure" already superseded by a successful send of the same message?
/// </summary>
/// <remarks>
/// <para>🔴 <b>The discovery (§655.3).</b> <c>SendMaxAttempts</c> retries inside a single send and
/// writes a SEPARATE log row per attempt. A message that fails once and succeeds a second later
/// therefore leaves BOTH a Failed row and a Sent row. Per Larsen's hotel calendar invite did exactly
/// that — failed 13:42:34, delivered 13:42:34 — so the "Failed" row the operator was asking about
/// had in fact reached him.</para>
///
/// <para>⇒ Two surfaces care, and they must agree: the retry job (which would otherwise send a
/// DUPLICATE) and the Comms page (which would otherwise report a delivered message as undelivered).
/// The rule lives here once, because §637 is what happens when the same judgement is written twice
/// and only one copy gets fixed.</para>
///
/// <para>🔒 <b>Matched on participant + CATEGORY, not on the exact row.</b> The retry attempts share
/// a category (that is what makes them the same message) but are separate rows with their own ids,
/// so an id-based match would find nothing. Category is also always populated, where
/// <c>TemplateName</c> is blank on older rows.</para>
/// </remarks>
public static class SupersededSendDetector
{
    /// <summary>One successful send, reduced to what the comparison needs.</summary>
    public readonly record struct Delivery(int? ParticipantId, string? Category, DateTimeOffset SentAt);

    /// <summary>
    /// True when <paramref name="deliveries"/> contains a successful send of the same message, to
    /// the same person, at or after the failure.
    /// </summary>
    /// <remarks>
    /// "At or after" rather than strictly after: the successful retry can land in the SAME second as
    /// the failure it replaces — which is precisely what happened in the case that found this.
    /// </remarks>
    public static bool IsSuperseded(
        int? participantId, string? category, DateTimeOffset failedAt,
        IReadOnlyCollection<Delivery> deliveries)
    {
        if (participantId is null or <= 0) return false;
        if (deliveries.Count == 0) return false;

        foreach (var d in deliveries)
        {
            if (d.ParticipantId != participantId) continue;
            if (d.SentAt < failedAt) continue;
            if (!string.Equals(d.Category, category, StringComparison.OrdinalIgnoreCase)) continue;
            return true;
        }

        return false;
    }
}
