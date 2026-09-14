namespace CommunityHub.Core.Domain;

/// <summary>
/// §1165 — an organizer's reservation of a swag-catalogue item for one sponsor company.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-01: <i>"it must be possible to reserve and order so they become
/// unavailable"</i>, and — asked which — <b>organizer-only holds, with an expiry and a notice</b>.</para>
///
/// <para>🔑 <b>Why this exists at all, when the webshop already has stock.</b> Buying an item takes it
/// off the market on its own; that needed no code. A HOLD is the thing the shop cannot express: a
/// promise made <i>before</i> payment — the verbal "we'll take the power banks" at a meeting. Without
/// it that promise lives in somebody's memory and the item gets sold twice.</para>
///
/// <para>🔒 <b>Organizer-only, which is what makes it safe.</b> One writer means no race: two
/// sponsors cannot claim the same item, and nobody can strand an item by abandoning a checkout. That
/// was the deciding factor over sponsor self-service holds.</para>
///
/// <para>⚠️ <b>Every hold expires.</b> A hold with no end is indistinguishable from a sale and would
/// quietly keep an item off the market for ever after the sponsor changed their mind — which is the
/// failure mode of every reservation system that has ever been left unattended.</para>
///
/// <para>🔒 <b>Released, never deleted.</b> The row keeps who promised what to whom and when. That
/// history is the whole value when two people remember a meeting differently.</para>
/// </remarks>
public class SwagCatalogHold
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The WooCommerce product id of the catalogue item being held.</summary>
    /// <remarks>
    /// 🔑 The product id, not its name: a product renamed in the webshop must not orphan a hold, and
    /// the name is read live from the shop anyway.
    /// </remarks>
    public long ProductId { get; set; }

    /// <summary>
    /// The product name AS IT READ when the hold was made — for the record, not for matching.
    /// </summary>
    /// <remarks>
    /// ⚠️ Deliberately a snapshot. If a product is renamed or deleted in the webshop, a hold that
    /// could only say "product 10471" would be unreadable exactly when someone is trying to work out
    /// what was promised.
    /// </remarks>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>Company Manager / WooCommerce company id the item is held for.</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>Company name at the time of the hold — again a snapshot, for reading back.</summary>
    public string CompanyName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Which organizer made the promise. The reason the history is worth keeping.</summary>
    public string CreatedByEmail { get; set; } = string.Empty;

    /// <summary>When the hold lapses if nothing is bought.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Free-text context — "verbal agreement at the Nov meeting".</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Set when the hold ends — expired by the sweep, released by hand, or fulfilled by an order.
    /// Null while the hold is live.
    /// </summary>
    /// <remarks>
    /// 🔒 This is what the "one live hold per item" unique index filters on, so an EXPIRED hold must
    /// actually be released by the sweep rather than merely being in the past. A row that is expired
    /// but not released would block the item for ever — the exact bug the expiry was added to prevent.
    /// </remarks>
    public DateTimeOffset? ReleasedAt { get; set; }

    public string? ReleasedByEmail { get; set; }

    /// <summary>Why it ended, in words, for the same reason the row is kept at all.</summary>
    public string? ReleasedReason { get; set; }

    /// <summary>
    /// When the "this hold is about to lapse" notice was sent, so it is sent once and not on every
    /// sweep tick.
    /// </summary>
    public DateTimeOffset? ExpiryNoticeSentAt { get; set; }

    /// <summary>A hold is live when it has not ended and has not run out.</summary>
    public bool IsLiveAt(DateTimeOffset now) => ReleasedAt is null && ExpiresAt > now;
}
