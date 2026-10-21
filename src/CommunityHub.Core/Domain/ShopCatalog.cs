namespace CommunityHub.Core.Domain;

/// <summary>
/// §1305 — the sponsor-facing GROUP a shop category belongs to: the handful of doors a sponsor walks
/// through ("Booths, Furniture, Swags, Package handling …") instead of the webshop's thirty categories.
/// </summary>
/// <remarks>
/// Operator 2026-09-26: <i>"guide them in their ordering process, so they can use categories like
/// Booths, Furniture, Swags, Package handling … Use existing categories from current Webshop and group
/// them."</i> The webshop categories stay as they are; a group is a label ON a category, so the shop's
/// own structure is kept and a regrouping is a data edit, not a code change.
/// </remarks>
public enum ShopGroup
{
    /// <summary>Not yet placed. Shown to organizers, never to sponsors.</summary>
    Unassigned = 0,

    /// <summary>Tier packages with an exhibitor booth (Platinum, Diamond, Gold, Feature, Shared).</summary>
    Booths = 1,

    /// <summary>Digital-only tier packages (Silver) and other logo-only sponsorships.</summary>
    Packages = 2,

    /// <summary>Branded feature packages — coffee, badges, lounge, party, …</summary>
    Branding = 3,

    /// <summary>Sponsor sessions and pre-day track sponsorships — the timeslot products.</summary>
    Sessions = 4,

    /// <summary>Booth furniture — chairs, tables, counters. The tier's included-furniture allowance applies here.</summary>
    Furniture = 5,

    /// <summary>Freight, customs and on-site handling of the sponsor's goods.</summary>
    PackageHandling = 6,

    /// <summary>The swag catalogue — branded items for the attendee bags.</summary>
    Swag = 7,

    /// <summary>Ticket packs.</summary>
    Tickets = 8,

    /// <summary>Booth options that are not furniture — TV screen, extra staff tickets, bag packaging (operator
    /// 2026-09-26: furniture and booth options as two categories).</summary>
    BoothOptions = 9,

    /// <summary>
    /// Never shown to sponsors; only an organizer can put it in a cart. The "Swag Sponsor" product (operator 2026-09-26:
    /// "it is used when 2 sponsor give us swags for free").
    /// </summary>
    OrganizerOnly = 10,
}

/// <summary>§1305 — the lifecycle state of a catalogue product, mirroring the webshop's own.</summary>
public enum ShopProductStatus
{
    /// <summary>Visible to sponsors.</summary>
    Published = 0,

    /// <summary>
    /// Hidden from sponsors. ⚠️ The live shop keeps TEST and DEV products as <c>private</c> (measured
    /// 2026-09-26: a "(TEST)" timeslot and a "[DEV]" booth), so private must never leak into a
    /// sponsor view.
    /// </summary>
    Private = 1,

    /// <summary>Half-finished in the webshop. Hidden from sponsors.</summary>
    Draft = 2,

    /// <summary>
    /// No longer returned by the webshop. Kept rather than deleted: an order line or a reservation
    /// may still point at it, and a row that vanished would make that history unreadable.
    /// </summary>
    Removed = 3,
}

/// <summary>
/// §1305 — one category of the sponsor shop, imported from the webshop.
/// </summary>
public class ShopCategory
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The webshop's category id. Null for a category created in CEH.</summary>
    public long? WooCategoryId { get; set; }

    /// <summary>The parent category (webshop categories are a tree, two levels deep today).</summary>
    public int? ParentId { get; set; }
    public ShopCategory? Parent { get; set; }

    /// <summary>Display name, HTML-decoded (the shop returns <c>Community &amp;amp; Appreciation</c>).</summary>
    public string Name { get; set; } = string.Empty;

    public string? Slug { get; set; }
    public string? DescriptionHtml { get; set; }

    /// <summary>Sort order within its parent, taken from the webshop's <c>menu_order</c>.</summary>
    public int SortOrder { get; set; }

    /// <summary>The sponsor-facing group. See <see cref="ShopGroup"/>.</summary>
    public ShopGroup Group { get; set; }

    /// <summary>
    /// True once an organizer has set <see cref="Group"/> by hand; the import then stops re-deriving it.
    /// </summary>
    public bool GroupSetByOrganizer { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// §1305 — one product of the sponsor shop.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-26: <i>"take the products from the current Webshop – same text, same
/// pictures, same prices, same availability – and migrate them over. During the period, where we
/// support both platforms, availability must be updated both places."</i></para>
///
/// <para>🔑 <b>While both shops run, the WEBSHOP owns the content and the stock count.</b> The import
/// overwrites text, price and status on every run unless <see cref="ContentManagedInCeh"/> is set.
/// Availability is never read from this row when the shop is reachable: <c>ShopStockService</c> asks the
/// shop live (operator: <i>"ceh and woocommerce must have same availability"</i>), and a CEH sale or
/// release changes the shop's count at that moment, so the shop stays the single counter. The stock
/// fields here are the last synced copy — the fallback when the shop cannot be reached.</para>
/// </remarks>
public class ShopProduct
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The webshop's product id. Null for a product created in CEH.</summary>
    public long? WooProductId { get; set; }

    /// <summary>Product name, HTML-decoded.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Slug { get; set; }

    /// <summary>
    /// The teaser. ⚠️ Raw HTML from WordPress: untrusted markup, sanitised before it is rendered.
    /// </summary>
    public string? ShortDescriptionHtml { get; set; }

    /// <summary>The full description. ⚠️ Raw HTML from WordPress, as above.</summary>
    public string? DescriptionHtml { get; set; }

    /// <summary>
    /// The list price in EUR, excluding VAT. The shop's prices are all EUR ("All prices are excl.
    /// Danish VAT (25%) for Danish companies").
    /// </summary>
    public decimal PriceEur { get; set; }

    /// <summary>
    /// §1305 — a discounted price the organizer set in CEH (operator 2026-09-26: "option to add discount and mark it with
    /// discount marking, so people can see normal price and new price"). CEH-only: the webshop sync never writes it and
    /// CEH never sends it anywhere. Ignored unless it is lower than <see cref="PriceEur"/>.
    /// </summary>
    public decimal? SalePriceEur { get; set; }

    /// <summary>
    /// §1305 — the product's place (1–3) in the shop's COMMERCIAL CORNER, or null. Operator 2026-09-26: <i>"a
    /// commercial corner in the webshop, where i can promote 3 products that i want to get sold, i need an organizer page
    /// for that and info when one is sold so i can add a new one"</i>. CEH-only.
    /// </summary>
    public int? PromotedSlot { get; set; }

    /// <summary>
    /// §1305 — for a SWAG product: the sponsor's share of the item's total cost, in %; ELDK covers the rest. Null ⇒ the
    /// edition default (<c>shop.&lt;edition&gt;.json → swagPricing.sponsorContributionPercent</c>, 33). Operator 2026-09-26:
    /// <i>"the % for swags are individual per product, default 33% … then other part is covered by ELDK"</i>. CEH-only.
    /// </summary>
    public decimal? SponsorContributionPercent { get; set; }

    /// <summary>True when a valid discount is set.</summary>
    public bool IsDiscounted => SalePriceEur is decimal s && s >= 0 && s < PriceEur;

    /// <summary>
    /// §1312 — is this product listed under <paramref name="group"/>? Its own group, and ALSO the Booths group for any
    /// sponsor-visible product that includes a booth (operator 2026-09-27: "i should have 6 available booths" — the lounge
    /// sponsorship with booth E-27 sits in Branding, so the Booths tab never showed it). Organizer-only products never.
    /// </summary>
    public bool ListedUnder(ShopGroup group) =>
        PrimaryCategory?.Group == group
        || (group == ShopGroup.Booths && !string.IsNullOrWhiteSpace(BoothId) && PrimaryCategory?.Group != ShopGroup.OrganizerOnly);

    /// <summary>What the sponsor pays per unit: the discounted price when there is one.</summary>
    public decimal EffectivePriceEur => IsDiscounted ? SalePriceEur!.Value : PriceEur;

    /// <summary>The discount as a whole percentage (for the badge), or 0.</summary>
    public int DiscountPercent => IsDiscounted && PriceEur > 0 ? (int)Math.Round((1 - SalePriceEur!.Value / PriceEur) * 100m) : 0;

    public ShopProductStatus Status { get; set; }

    /// <summary>
    /// True when the webshop counts this product (booths, sessions, branded packages: one each).
    /// False ⇒ unlimited (furniture, package handling, options).
    /// </summary>
    public bool ManageStock { get; set; }

    /// <summary>How many remain, when <see cref="ManageStock"/>. Null when the shop does not count.
    /// 🔴 §1312 — CEH is the one stock counter; a concurrency check stops two sales of the last unit at once.</summary>
    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
    public int? StockQuantity { get; set; }

    /// <summary>
    /// The shop's own verdict. ⚠️ Anything not recognised as in stock is stored as NOT in stock:
    /// offering an item we cannot confirm is sellable is the expensive direction of the two.
    /// </summary>
    public bool InStock { get; set; }

    /// <summary>The booth this product sells, parsed from its name ("… - Booth E-20A (Shared)").</summary>
    public string? BoothId { get; set; }

    /// <summary>The timeslot this product sells, parsed from its name ("timeslot: 2" / "timeslot: early").</summary>
    public string? Timeslot { get; set; }

    /// <summary>The estimated time of that slot, as the shop states it ("11:05-11:25").</summary>
    public string? TimeslotTime { get; set; }

    /// <summary>The most specific category, for grouping. The full set is <see cref="Categories"/>.</summary>
    public int? PrimaryCategoryId { get; set; }
    public ShopCategory? PrimaryCategory { get; set; }

    public int SortOrder { get; set; }

    /// <summary>
    /// When true the import leaves text, price and status alone: an organizer has taken the product
    /// over in CEH. Stock is still synced — stock is shared, content is not.
    /// </summary>
    public bool ContentManagedInCeh { get; set; }

    /// <summary>The shop's last-modified stamp at the last import.</summary>
    public DateTimeOffset? WooModifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }

    public List<ShopProductCategory> Categories { get; set; } = new();
    public List<ShopProductImage> Images { get; set; } = new();
}

/// <summary>§1305 — a product's membership of a category (a product sits in parent AND child).</summary>
public class ShopProductCategory
{
    public int ProductId { get; set; }
    public ShopProduct Product { get; set; } = null!;

    public int CategoryId { get; set; }
    public ShopCategory Category { get; set; } = null!;
}

/// <summary>
/// §1305 — one picture of a product, copied from the webshop into the SharePoint product folder.
/// </summary>
/// <remarks>
/// Operator 2026-09-26: <i>"Use sharepoint to store pictures/products … Events\ELDK 2027\EventHub\
/// Sponsors\Webshop\Products."</i> The shop's URL is kept as <see cref="SourceUrl"/> so a re-import
/// copies a picture only when it changed, and so the picture can still be traced to where it came from.
/// </remarks>
public class ShopProductImage
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public ShopProduct Product { get; set; } = null!;

    public int SortOrder { get; set; }

    /// <summary>Where the picture came from (the webshop's media URL), or <see cref="CehUploadPrefix"/> + an id for
    /// a picture an organizer uploaded in CEH.</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>§1305 — marks a picture uploaded in the CEH product editor (it lives only in SharePoint).</summary>
    public const string CehUploadPrefix = "ceh-upload:";

    public bool IsCehUpload => SourceUrl.StartsWith(CehUploadPrefix, StringComparison.Ordinal);

    public string? AltText { get; set; }

    /// <summary>The drive-relative path of the SharePoint copy. Null until it has been copied.</summary>
    public string? SharePointPath { get; set; }

    /// <summary>The Graph item id of the SharePoint copy — what the picture endpoint downloads.</summary>
    public string? SharePointItemId { get; set; }

    public string? ContentType { get; set; }

    /// <summary>Why the last copy attempt failed, when it did. Cleared on success.</summary>
    public string? CopyError { get; set; }

    public DateTimeOffset? CopiedAt { get; set; }
}
