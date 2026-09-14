using System.Security.Cryptography;

namespace CommunityHub.Core.Domain;

/// <summary>§1040 — what a monitor watches.</summary>
public enum AttendeeMonitorKind
{
    /// <summary>Every attendee whose e-mail address ends with the configured domain.</summary>
    EmailDomain = 0,

    /// <summary>Every attendee on an order carrying the configured coupon / promo code.</summary>
    CouponCode = 1,

    /// <summary>
    /// §1093 — every attendee who used <b>ANY</b> coupon billed to this e-conomic customer.
    /// <see cref="AttendeeMonitor.Value"/> holds the customer number.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19: *"it could be great that the monitor is linked per billing
    /// customer, so arrow gets one link that shows any usage for any coupons they have"*.</para>
    ///
    /// <para>🔑 <b>Why this is the right grain and <see cref="CouponCode"/> is not.</b> A partner
    /// thinks in "my usage", not in promo codes: Arrow may have several codes across ticket classes
    /// and campaigns, and one link per code means several links, each showing a fragment, none of
    /// them answering the question they actually have. The billing customer is also exactly what the
    /// INVOICE is addressed to — so the monitor and the invoice describe the same population, which
    /// is the property that makes the page usable as a check on the bill.</para>
    ///
    /// <para>🔒 Scope is still bounded by construction: a customer's monitor can only ever widen to
    /// coupons that are billed to that same customer, and adding a coupon to a different customer
    /// cannot pull rows into this one.</para>
    /// </remarks>
    ErpCustomer = 2,
}

/// <summary>
/// §1040 — A SHAREABLE, READ-ONLY VIEW OF WHO HAS BOUGHT A TICKET, for one company.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10: <i>"we have a poduct called Volume Package, which is valid for any
/// company buying more than 10 tickets … i want to be able to generate a secure link per entry
/// (monitors), which i can send to persons where they can follow who bought a ticket"</i>.</para>
///
/// <para>A company that has bought a block of tickets wants to watch its own people redeem them,
/// without a hub login and without seeing anybody else's attendees. A monitor is that scope,
/// expressed as either an e-mail DOMAIN or a COUPON code, plus the token that opens it.</para>
///
/// <para>🔴 <b>THIS ROW GRANTS ACCESS TO PERSONAL DATA WITHOUT A LOGIN.</b> The page it opens lists
/// real people's names and e-mail addresses to whoever holds the link. Everything below exists
/// because of that, not as ceremony:</para>
///
/// <list type="bullet">
///   <item><see cref="Token"/> is 256 bits of cryptographic randomness — guessing is not a
///   threat model, so the URL itself is the credential and must be worth trusting.</item>
///   <item><see cref="RevokedAt"/> makes the link WITHDRAWABLE. A share that cannot be taken back is
///   a permanent disclosure, and "delete the row" is not the same thing — it loses the audit of who
///   was given what.</item>
///   <item>The scope is ONE domain or ONE coupon. A token can never widen to another company's
///   attendees, so the blast radius of a forwarded link is bounded by construction.</item>
/// </list>
/// </remarks>
public class AttendeeMonitor
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>What an organizer calls this, e.g. "Globeteam volume package". Shown on both pages.</summary>
    public string Name { get; set; } = string.Empty;

    public AttendeeMonitorKind Kind { get; set; }

    /// <summary>
    /// The domain (<c>globeteam.com</c>) or coupon code (<c>ELDK27VOLUME</c>) this monitor matches.
    /// </summary>
    /// <remarks>
    /// 🔑 Stored NORMALISED — lower-cased, trimmed, and for a domain with any leading <c>@</c> or
    /// <c>*@</c> removed. An organizer who types "@Globeteam.com " and one who types "globeteam.com"
    /// mean the same monitor, and a match that depends on which they typed is a bug report waiting
    /// to happen the day someone's tickets do not appear.
    /// </remarks>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// The secret in the URL. 🔒 Unique, and the ONLY thing standing between the internet and this
    /// company's attendee list.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Set when an organizer withdraws the link; the page then refuses it.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// §1040 — when the link stops working by itself. Operator 2026-08-10:
    /// <i>"link must be active until 15 feb 2027 (after event)"</i>.
    /// </summary>
    /// <remarks>
    /// 🔑 An expiry is the half of the safety that does not depend on anyone remembering. Revocation
    /// covers *"take this back now"*; this covers the far more likely case — a link shared with a
    /// partner, the event ends, and nobody ever thinks about it again. ⚠️ Deliberately AFTER the
    /// event (2027-02-09/10), not on it: people chase attendance questions for days afterwards.
    /// </remarks>
    public DateTimeOffset? ExpiresAt { get; set; } = DefaultExpiry;

    /// <summary>The default he specified — the day after the edition, plus a week's grace.</summary>
    public static readonly DateTimeOffset DefaultExpiry =
        new(2027, 2, 15, 23, 59, 59, TimeSpan.Zero);

    /// <summary>True while the link still opens: not revoked, and not past its date.</summary>
    public bool IsActive => RevokedAt is null && !IsExpired(DateTimeOffset.UtcNow);

    /// <summary>Whether the link has passed its expiry at the given moment.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && now >= e;

    /// <summary>
    /// §1094 — when this customer was last sent their fortnightly usage report, or null for never.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19: *"they must get status per mail every 2 weeks of usage, cap
    /// (remaining) if set"*.</para>
    ///
    /// <para>🔑 <b>The stamp lives on the MONITOR, not on the coupon</b>, because the report is per
    /// BILLING CUSTOMER — the same grain as the link it carries. On a coupon it would send one mail
    /// per code, so a partner with three codes would get three reports a fortnight, each showing a
    /// fragment of the picture the link already shows whole.</para>
    ///
    /// <para>🔒 It is the ONLY thing preventing a repeat: a fortnightly job with no memory sends on
    /// every tick. Stamped after the send, never before.</para>
    /// </remarks>
    public DateTimeOffset? UsageReportSentAt { get; set; }

    /// <summary>
    /// §1094 — when this partner last asked for their cap to be raised, and to what.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19: *"customer should be able to extend the cap using the status mail
    /// they get, so they can extend from 50 -> 60 as example. then i must get a email so i can
    /// extend it in zoho + i can invoice, if it is a prepaid order"*.</para>
    ///
    /// <para>🔴 <b>A REQUEST, NEVER A SELF-SERVICE RAISE — and that is forced by where the cap really
    /// lives.</b> He was explicit that *"cap is controlled / limited by zoho"* and CEH's number
    /// merely matches it. Backstage has no coupon API (§787.14), so a partner clicking "extend"
    /// cannot change the limit that actually stops them claiming. If CEH raised its own number
    /// anyway, our reports would promise 60 while Zoho still refused at 50 — the hub would be lying
    /// on the partner's own page. ⇒ The click asks; the operator raises it in Zoho and in CEH, and
    /// invoices it when the agreement is prepaid.</para>
    ///
    /// <para>🔒 Stamped so the request is auditable and so a partner refreshing the page cannot mail
    /// the ops box repeatedly — this endpoint is anonymous, and the only thing between it and an
    /// inbox is this throttle.</para>
    /// </remarks>
    public DateTimeOffset? LastCapRequestAt { get; set; }

    /// <inheritdoc cref="LastCapRequestAt"/>
    public int? LastCapRequestedTickets { get; set; }

    public string? CreatedByEmail { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the shared page was last opened, and how often — so an organizer can see whether a link
    /// is in use before revoking it, and notice one being read that should not be.
    /// </summary>
    public DateTimeOffset? LastViewedAt { get; set; }
    public int ViewCount { get; set; }

    /// <summary>
    /// 256 bits, URL-safe, from a cryptographic RNG.
    /// </summary>
    /// <remarks>
    /// ⚠️ <see cref="RandomNumberGenerator"/>, never <c>Guid.NewGuid()</c>: a GUID is a uniqueness
    /// primitive, not a secrecy one — v4 exposes 122 bits with no guarantee about predictability,
    /// and it is a habit worth not forming on the one field that IS the credential.
    /// </remarks>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    /// <summary>
    /// Normalises what an organizer typed into what is matched against. See <see cref="Value"/>.
    /// </summary>
    public static string NormaliseValue(AttendeeMonitorKind kind, string? raw)
    {
        var v = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (kind != AttendeeMonitorKind.EmailDomain) return v;

        // "@globeteam.com" / "*@globeteam.com" / "globeteam.com" all mean the same domain.
        var at = v.LastIndexOf('@');
        if (at >= 0) v = v[(at + 1)..];
        return v.Trim();
    }
}
