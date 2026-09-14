using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>What a viewer may do with a catalogue item right now.</summary>
public enum SwagItemState
{
    /// <summary>Free: in stock and not reserved by anybody.</summary>
    Available = 0,

    /// <summary>Reserved for the company that is looking at it.</summary>
    HeldForYou = 1,

    /// <summary>Reserved for a different company.</summary>
    HeldForSomeoneElse = 2,

    /// <summary>Out of stock in the webshop — bought, or withdrawn from sale.</summary>
    Taken = 3,
}

/// <summary>
/// §1165 — the single rule for whether a catalogue item can be taken.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Two systems answer half the question each</b>, and this is the one place that joins
/// them. The webshop owns <i>sold</i> — real stock, the same mechanism the session slots already use.
/// CEH owns <i>promised</i> — the organizer hold, which is the thing a shop cannot express because it
/// happens before payment.</para>
///
/// <para>🔒 <b>Joined here and nowhere else.</b> The sponsor page and the organizer roll-up both ask
/// this function. A second copy of the rule in a page is how the two would come to disagree about
/// whether an item is free — and the visible symptom would be a sponsor being offered something the
/// organizer had already promised to someone.</para>
/// </remarks>
public static class SwagAvailability
{
    /// <summary>
    /// Resolve an item's state for one viewer.
    /// </summary>
    /// <param name="inStock">The webshop's answer.</param>
    /// <param name="liveHold">The live hold on this item, if any.</param>
    /// <param name="viewerCompanyId">
    /// The company looking. Null or blank for an organizer, who is nobody's company — so a hold
    /// always reads as "held for someone", never as "held for you".
    /// </param>
    public static SwagItemState Resolve(
        bool inStock, SwagCatalogHold? liveHold, string? viewerCompanyId)
    {
        // ⚠️ SOLD BEATS HELD. An item that is out of stock is gone whoever it was promised to, and
        // saying "reserved for you" about something nobody can buy would be the crueller of the two
        // wrong answers.
        if (!inStock) return SwagItemState.Taken;

        if (liveHold is null) return SwagItemState.Available;

        return string.Equals(
            liveHold.SponsorCompanyId?.Trim(),
            (viewerCompanyId ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(viewerCompanyId)
            ? SwagItemState.HeldForYou
            : SwagItemState.HeldForSomeoneElse;
    }

    /// <summary>Whether this viewer can act on the item — buy it, or take up their reservation.</summary>
    /// <remarks>
    /// 🔑 <c>HeldForYou</c> is buyable: the hold exists to keep the item FOR them, so it must not
    /// also be the thing that stops them taking it.
    /// </remarks>
    public static bool CanTake(SwagItemState state) =>
        state is SwagItemState.Available or SwagItemState.HeldForYou;
}
