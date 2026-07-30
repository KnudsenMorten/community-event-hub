using System.Text;
using CommunityHub.Core.Integrations;

namespace CommunityHub.Core.Tasks.Data;

/// <summary>
/// §687.1 — shows a sponsor WHICH shipment services they have already bought, on the Pre-Event
/// Shipment task.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29: <i>"it would be great to list the package handling products a sponsor
/// have bought so they know if they need to buy more"</i>. With 19 separate Package Handling
/// products (pre-event handling, post-event handling, storage, customs clearance, airfreight), a
/// sponsor genuinely cannot remember what they ordered — and the cost of guessing wrong is a pallet
/// stuck at the venue.</para>
///
/// <para>🔒 <b>Reads the SAME <see cref="SponsorPurchaseSummaryService"/> as the organizer's §687
/// Logistics panel.</b> The sponsor's list and the operator's list are the same list; there is no
/// second query to drift.</para>
///
/// <para>🔒 <b>THE CATEGORY, not a product id list.</b> There are 19 today and the operator expects
/// more — pinning ids would silently miss the next one, which on this task means telling a sponsor
/// they have bought nothing when they have.</para>
/// </remarks>
public sealed class ShipmentPurchasesTaskDataProvider : CategoryPurchasesTaskDataProvider
{
    public ShipmentPurchasesTaskDataProvider(SponsorPurchaseSummaryService purchases)
        : base(purchases) { }

    public override string Source => "shipmentPurchases";

    /// <summary>Verified live against the production webshop 2026-07-29 (category id 80).</summary>
    protected override string Category => "Package Handling";

    protected override string CannotCheckMessage =>
        "We could not check your webshop orders right now, so we cannot show which shipment "
        + "services you have already bought. Please check your order confirmation, or come back "
        + "shortly.";

    protected override string NothingBoughtMarkup =>
        ":::callout info\n"
        + "**No shipment order detected** — we found nothing in your webshop orders under "
        + "**Package Handling**.\n\n"
        + "That is fine if you are not shipping anything to your booth, or are using your own "
        + "freight company.\n"
        + ":::";

    protected override string BoughtLeadIn =>
        "We checked your webshop orders — you have already booked these shipment services:";
}
