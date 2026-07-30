using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Tasks.Data;

/// <summary>
/// §666 — tells a sponsor whether they have ALREADY BOUGHT a booth TV, and how many.
/// </summary>
/// <remarks>
/// <para><b>What this replaces.</b> The TV Rental task said <i>"NOTE: Check your contract to see
/// whether you have already booked a TV."</i> — it asked the sponsor to go and look up something the
/// hub already knows. Operator 2026-07-29: <i>"it must enumerate all the orders sponsor made to
/// check for the specific product number which is the TV. Change the word, so you help the sponsor
/// so he already get this info: did he buy TV (amount) or did he not."</i></para>
///
/// <para>🔒 <b>This is the reason §666 was blocked on the redesign, and the proof it needed to
/// be.</b> The old model resolved <c>{{placeholders}}</c> during the WooCommerce pull and stored the
/// finished prose in <c>Tasks.Description</c>, so the count would be frozen at the last pull — and,
/// far worse, a FAILED webshop lookup would have PERSISTED "you have not booked a TV". By render
/// time there was no longer any memory that the lookup had failed. Here the answer is computed when
/// the page renders and the three states are a TYPE, so the failure case cannot be forgotten.</para>
///
/// <para>🔒 <b>Every uncertainty lands on "we could not check".</b> Integration disabled, product id
/// unset, no company on the session, an API error — all of them mean we do not know, and none of
/// them may render as "you have not booked a TV". §666 is explicit about the cost: that sentence
/// pushes a sponsor into buying a second one.</para>
/// </remarks>
public sealed class TvPurchaseTaskDataProvider : ITaskDataProvider
{
    /// <summary>The participant-facing sentence for every "we do not know" path.</summary>
    private const string CannotCheck =
        "We could not check your webshop orders right now, so we cannot tell you whether you have "
        + "already booked a TV screen. Please check your order confirmation, or come back shortly.";

    /// <summary>
    /// The dedicated WooCommerce category, created by the operator 2026-07-29 (id 99) and verified
    /// live.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>This replaces the pinned product id 10635, and it retires §666's worst constraint.</b>
    /// §666 had to match on the numeric id because the TV product's SKU is EMPTY and matching on the
    /// display name would break the moment anyone renamed it — leaving the id as "the only stable key
    /// available". A dedicated category is a better key than either: it survives renames AND it
    /// survives the product being replaced or split into several TV options next edition, which an
    /// id cannot.
    /// </remarks>
    public const string Category = "TV Rental";

    private readonly SponsorPurchaseSummaryService _purchases;
    private readonly WooCommerceOptions _options;
    private readonly ILogger<TvPurchaseTaskDataProvider> _log;

    public TvPurchaseTaskDataProvider(
        SponsorPurchaseSummaryService purchases,
        WooCommerceOptions options,
        ILogger<TvPurchaseTaskDataProvider> log)
    {
        _purchases = purchases;
        _options = options;
        _log = log;
    }

    public string Source => "tvPurchase";

    public async Task<TaskDataResult> ResolveAsync(TaskDataContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.SponsorCompanyId))
        {
            return TaskDataResult.Unavailable(CannotCheck, "no sponsor company on the context");
        }

        if (!_options.Enabled)
        {
            return TaskDataResult.Unavailable(CannotCheck, "WooCommerce integration is disabled");
        }

        // 🔒 §687 — THE SAME SERVICE the organizer's Logistics TV panel reads. The sponsor is told
        // how many they booked; the operator is told how many to order from the venue. Those two
        // numbers must be the same number, and two independent queries over the same orders is
        // exactly how they drift — invisibly, because each screen stays internally consistent.
        var summary = await _purchases.ByCategoryAsync(Category, ct);

        if (summary.CouldNotCheck)
        {
            return TaskDataResult.Unavailable(CannotCheck, summary.Reason);
        }

        var quantity = summary.Rows
            .Where(r => string.Equals(r.CompanyId, context.SponsorCompanyId, StringComparison.Ordinal))
            .Sum(r => r.Quantity);

        if (quantity <= 0)
        {
            return TaskDataResult.NotFound(
                ":::callout info\n"
                + "We checked your webshop orders: you have **not booked a TV screen** yet. "
                + "If you want one, order it below.\n"
                + ":::");
        }

        // 🔒 The count matters, not just yes/no — he asked for the amount because a sponsor may have
        // bought two, and someone reading "you have a TV" with two booths would order a third.
        var screens = quantity == 1 ? "**1 TV screen**" : $"**{quantity} TV screens**";
        return TaskDataResult.Found(
            ":::callout info\n"
            + $"We checked your webshop orders: you have already booked {screens} for your booth. "
            + "You only need to order again if you want another one.\n"
            + ":::");
    }
}
