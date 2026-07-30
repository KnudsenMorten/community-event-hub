using System.Text;
using CommunityHub.Core.Integrations;

namespace CommunityHub.Core.Tasks.Data;

/// <summary>
/// §687.1 / §687.3 — "here is what you have ALREADY BOUGHT from this product category", for any task
/// whose category has many products.
/// </summary>
/// <remarks>
/// <para><b>Why a base class rather than a third copy.</b> This is now the pattern for shipment
/// (19 Package Handling products) and booth furniture (Booth Option), and the operator keeps finding
/// more places it applies — each one is *"list what they bought so they know if they need to buy
/// more"*. Three near-identical providers would be three places for the §555 three-state contract to
/// be got subtly wrong, which is the whole class of bug this redesign exists to remove.</para>
///
/// <para>🔒 <b>Matched on the CATEGORY, never a list of product ids.</b> These categories grow — the
/// operator adds products as prices firm up. A pinned id list silently misses the newest product,
/// and on these tasks that means telling a sponsor they bought nothing when they did.</para>
///
/// <para>🔒 All three states are handled HERE, once: an answer, a positive "you have not bought
/// any", and "we could not check". A subclass supplies only the category and its copy.</para>
/// </remarks>
public abstract class CategoryPurchasesTaskDataProvider : ITaskDataProvider
{
    private readonly SponsorPurchaseSummaryService _purchases;

    protected CategoryPurchasesTaskDataProvider(SponsorPurchaseSummaryService purchases)
    {
        _purchases = purchases;
    }

    /// <summary>The directive name — <c>:::data {Source}</c>.</summary>
    public abstract string Source { get; }

    /// <summary>The WooCommerce product category to total.</summary>
    protected abstract string Category { get; }

    /// <summary>What we could not check, in the sponsor's terms. Completes "…so we cannot show X".</summary>
    protected abstract string CannotCheckMessage { get; }

    /// <summary>The "you have not bought any" sentence, as body markup.</summary>
    protected abstract string NothingBoughtMarkup { get; }

    /// <summary>Lead-in for the list of what they DID buy.</summary>
    protected abstract string BoughtLeadIn { get; }

    public async Task<TaskDataResult> ResolveAsync(TaskDataContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.SponsorCompanyId))
        {
            return TaskDataResult.Unavailable(CannotCheckMessage, "no sponsor company on the context");
        }

        var summary = await _purchases.ByCategoryAsync(Category, ct);
        if (summary.CouldNotCheck)
        {
            return TaskDataResult.Unavailable(CannotCheckMessage, summary.Reason);
        }

        var mine = summary.Rows.FirstOrDefault(
            r => string.Equals(r.CompanyId, context.SponsorCompanyId, StringComparison.Ordinal));

        // 🔒 A POSITIVE statement, not an empty gap. Many of these products are still DRAFTS in the
        // webshop, so "nothing bought" is the normal answer today — it must read as a real answer
        // rather than as something that failed to load.
        if (mine is null || mine.Lines.Count == 0) return TaskDataResult.NotFound(NothingBoughtMarkup);

        var sb = new StringBuilder(":::callout info\n").Append(BoughtLeadIn).Append("\n\n");
        foreach (var line in mine.Lines)
        {
            // Quantity first so the numbers line up down the left edge when scanning.
            sb.Append("* **").Append(line.Quantity).Append("×** ").Append(line.ProductName).Append('\n');
        }
        sb.Append("\nOrder below if you need anything else.\n:::");

        return TaskDataResult.Found(sb.ToString());
    }
}

/// <summary>
/// §687.3 — the booth FURNITURE a sponsor has already ordered.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29, on the Booth Option products: same ask as shipment — show them what
/// they have so they know whether to buy more. It matters more here than it looks: the booth-layout
/// task's own copy warns *"You must place the order even if the furniture is already included —
/// that is what reserves it for you"*, so a sponsor genuinely cannot tell from their package alone
/// whether they have chairs coming.</para>
/// </remarks>
public sealed class BoothFurniturePurchasesTaskDataProvider : CategoryPurchasesTaskDataProvider
{
    public BoothFurniturePurchasesTaskDataProvider(SponsorPurchaseSummaryService purchases)
        : base(purchases) { }

    public override string Source => "boothFurniturePurchases";

    /// <summary>
    /// Verified live 2026-07-29 (category id 102).
    /// </summary>
    /// <remarks>
    /// 🔒 <b>`Booth Furniture`, NOT `Booth Option` — and this is §687.6, fixed.</b> `Booth Option`
    /// is the operator's UMBRELLA grouping and still sits on all of these products; it also holds
    /// the TV screen (10635), the attendee-bag packaging (10633) and the extra-staff tickets
    /// (10634), each of which belongs to a DIFFERENT task. Totalling the umbrella would have listed
    /// a bag insert as booth furniture, and — once completion derives from purchases — marked a
    /// sponsor's furniture task Done for someone who bought no chairs. He split the categories for
    /// exactly this reason: <i>"they are all part of booth options, so dont use that"</i>.
    /// </remarks>
    protected override string Category => "Booth Furniture";

    protected override string CannotCheckMessage =>
        "We could not check your webshop orders right now, so we cannot show which booth furniture "
        + "you have already ordered. Please check your order confirmation, or come back shortly.";

    /// <remarks>
    /// 🔒 Names WHAT WE LOOKED FOR, on the operator's instruction 2026-07-29: <i>"you are also free
    /// to add info in the tasks like 'No order detected with product: …' - this makes it very
    /// informative"</i>. A bare "you have not ordered" leaves the sponsor unsure whether we looked
    /// at the right thing; naming the category makes the answer checkable against their own order
    /// confirmation.
    /// </remarks>
    protected override string NothingBoughtMarkup =>
        ":::callout warning\n"
        + "**No furniture order detected** — we found nothing in your webshop orders under "
        + "**Booth Furniture**.\n\n"
        + "🔒 The order is what RESERVES your furniture — **including the items already included in "
        + "your package**. This task completes on its own once your order is placed.\n"
        + ":::";

    protected override string BoughtLeadIn =>
        "We checked your webshop orders — you have already ordered this booth furniture:";
}

/// <summary>
/// §687.5 — whether the sponsor has bought the ATTENDEE-BAG PACKAGING service.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29: <i>"attendee packaging is also a product they must have bought to get
/// that service"</i>. The product is <c>Attendee Bag Content Packaging – ELDK27</c> (10633), now in
/// its own <c>Attendee Bag Packaging</c> category (id 100).</para>
///
/// <para>🔒 <b>This is the SECOND of two gates on that task, and they mean different things.</b>
/// [§676] made it a DECISION, which records INTENT — "we would like to contribute". Buying the
/// packaging is what actually gets their material into the bags. A sponsor can answer yes and never
/// buy it, and their brochures then sit in a box nobody packs. So the task shows BOTH: what they
/// answered, and whether the service is actually paid for.</para>
/// </remarks>
public sealed class AttendeeBagPackagingTaskDataProvider : CategoryPurchasesTaskDataProvider
{
    public AttendeeBagPackagingTaskDataProvider(SponsorPurchaseSummaryService purchases)
        : base(purchases) { }

    public override string Source => "attendeeBagPackaging";

    /// <summary>Verified live 2026-07-29 (category id 100).</summary>
    protected override string Category => "Attendee Bag Packaging";

    protected override string CannotCheckMessage =>
        "We could not check your webshop orders right now, so we cannot confirm whether the bag "
        + "packaging service is booked. Please check your order confirmation, or come back shortly.";

    protected override string NothingBoughtMarkup =>
        ":::callout warning\n"
        + "**No order detected with product: Attendee Bag Content Packaging – {{editionCode}}.**\n\n"
        + "Your materials can only go into the attendee bags once this service is booked — order it "
        + "in STEP 1 below.\n"
        + ":::";

    protected override string BoughtLeadIn =>
        "We checked your webshop orders — the bag packaging service is **booked**:";
}

/// <summary>
/// §687 — how many EXTRA exhibitor-staff tickets the sponsor has bought.
/// </summary>
/// <remarks>
/// The <c>Register booth members</c> task already tells sponsors to buy
/// <c>Discounted Tickets for Extra Exhibitor Staff</c> (10634, now its own category, id 101) when
/// they need more people than their package allows. Showing what they have bought turns "did I order
/// those extra passes?" into a fact instead of a trip to their order confirmation.
/// </remarks>
public sealed class ExtraStaffTicketsTaskDataProvider : CategoryPurchasesTaskDataProvider
{
    public ExtraStaffTicketsTaskDataProvider(SponsorPurchaseSummaryService purchases)
        : base(purchases) { }

    public override string Source => "extraStaffTickets";

    /// <summary>Verified live 2026-07-29 (category id 101).</summary>
    protected override string Category => "Extra Staff Tickets";

    protected override string CannotCheckMessage =>
        "We could not check your webshop orders right now, so we cannot show how many extra staff "
        + "tickets you have bought. Please check your order confirmation, or come back shortly.";

    protected override string NothingBoughtMarkup =>
        ":::callout info\n"
        + "**No order detected with product: Discounted Tickets for Extra Exhibitor Staff "
        + "{{editionCode}}.**\n\n"
        + "That is fine unless you need more people at the booth than your package includes.\n"
        + ":::";

    protected override string BoughtLeadIn =>
        "We checked your webshop orders — you have already bought these extra staff tickets:";
}
