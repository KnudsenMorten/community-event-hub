namespace CommunityHub.Core.Domain;

/// <summary>
/// §1165k — a euro credit granted to one sponsor, spendable on catalogue items.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-01: <i>"add option to provide a coupon/discount code i provide them with a
/// euro value"</i>.</para>
///
/// <para>🔑 <b>This row is the RECORD, not the money.</b> The coupon itself lives in the webshop —
/// the only place that can actually reduce a price at checkout — and CEH stores who was given what,
/// by whom, and what it was worth. That split is deliberate: a bug here can misreport a credit, but
/// it can never give money away, because CEH is never asked whether a discount is valid.</para>
///
/// <para>🔒 <b>Never deleted.</b> A credit is a commercial promise; the row keeps who made it and
/// when, which is the whole value the first time two people remember a conversation differently.
/// Withdrawing one sets <see cref="RevokedAt"/>.</para>
/// </remarks>
public class SwagCatalogCredit
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>Company Manager / WooCommerce company id the credit belongs to.</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>Company name when the credit was granted — a snapshot, for reading back.</summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>The coupon code the sponsor types at checkout.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>What it is worth, in the shop's currency.</summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// The webshop's id for the coupon, so the two can be reconciled later.
    /// </summary>
    /// <remarks>
    /// ⚠️ Null only if a grant were ever recorded without the shop confirming the coupon — which the
    /// service refuses to do. A credit CEH shows but the shop has never heard of is a promise that
    /// fails at checkout, in front of the sponsor.
    /// </remarks>
    public long? WooCouponId { get; set; }

    /// <summary>When the coupon stops working. Null only if granted with no expiry.</summary>
    public DateOnly? ExpiresOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Which organizer granted it — the reason the history is worth keeping.</summary>
    public string CreatedByEmail { get; set; } = string.Empty;

    /// <summary>Free-text context: what was agreed, and where.</summary>
    public string? Note { get; set; }

    /// <summary>Set when the credit is withdrawn. The row stays.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedByEmail { get; set; }

    public string? RevokedReason { get; set; }

    /// <summary>Live = granted, not withdrawn, and not past its expiry.</summary>
    public bool IsLiveOn(DateOnly today) =>
        RevokedAt is null && (ExpiresOn is null || ExpiresOn.Value >= today);
}
