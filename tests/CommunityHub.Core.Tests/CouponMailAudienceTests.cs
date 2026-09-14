using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1118 — every coupon mail a PAYING CUSTOMER receives is copied to the organizer mailbox.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-21: *"any coupon mails must put info@expertslive.dk as cc"*.</para>
///
/// <para>🔑 The coupon area is the one place CEH writes directly to a customer — a claim invite
/// quoting an invoice number, a status mail quoting what they will be billed. Everything else in the
/// area already goes to <c>info@</c>, so the organizer team could see every message ABOUT a partner
/// except the ones the partner actually got. When they reply *"your mail said 30"*, somebody has to
/// be able to read the mail.</para>
/// </remarks>
public sealed class CouponMailAudienceTests
{
    [Fact]
    public void A_customer_mail_is_copied_to_the_organizer_mailbox()
        => Assert.Equal(
            new[] { "info@expertslive.dk" },
            CouponMailAudience.CcFor("carsten@arrow.example"));

    [Theory]
    [InlineData("info@expertslive.dk")]
    [InlineData("  INFO@ExpertsLive.dk  ")]
    public void A_mail_already_ADDRESSED_to_the_mailbox_is_not_copied_to_it_as_well(string to)
        // 🔒 A duplicate is indistinguishable from a bug and trains people to skim. Null so the
        // caller's no-CC overload applies.
        => Assert.Null(CouponMailAudience.CcFor(to));
}
