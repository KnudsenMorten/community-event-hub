namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// 🔴 §1118 — every coupon mail that leaves the building is COPIED to the event-actions mailbox.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-21: *"any coupon mails must put info@expertslive.dk as cc"*.</para>
///
/// <para>🔑 <b>Why it matters here specifically.</b> The coupon area is the one place CEH writes
/// directly to a PAYING CUSTOMER — a claim invite quoting an invoice number, a status mail quoting
/// what they will be billed. Everything else in the area is addressed to <c>info@</c> already, so
/// until now the organizer team could see every message about a partner except the ones the partner
/// actually received. When Arrow replies *"your mail said 30"*, somebody has to be able to read the
/// mail.</para>
///
/// <para>🔒 <b>CC, not BCC.</b> The customer can see that the organizer team is on the thread, which
/// is true and is the point — replying to either address reaches the same people.</para>
///
/// <para>⚠️ <b>Only on the CUSTOMER-facing mails.</b> The ops notices (the Backstage instruction, the
/// cap request, the draft-invoice notice) are already addressed to this mailbox; CC-ing it as well
/// would deliver every one of them twice, which is how a mailbox stops being read.</para>
/// </remarks>
public static class CouponMailAudience
{
    /// <summary>The event-actions mailbox (§1075) — the same address the ops notices go to.</summary>
    public const string CopyTo = "info@expertslive.dk";

    /// <summary>
    /// The CC list for a customer-facing coupon mail, or null when the recipient IS the mailbox.
    /// </summary>
    /// <remarks>
    /// 🔒 Never CC an address the mail is already addressed to: a duplicate is indistinguishable
    /// from a bug and trains people to skim. Returns null so the caller's no-CC overload applies.
    /// </remarks>
    public static IReadOnlyCollection<string>? CcFor(string? toEmail) =>
        string.Equals((toEmail ?? string.Empty).Trim(), CopyTo, StringComparison.OrdinalIgnoreCase)
            ? null
            : new[] { CopyTo };
}
