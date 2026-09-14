using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1116 — no raw machine id where a person reads a name.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-21, on a real Arrow invoice printing <c>Ticket Class: 14880000003485482</c>:
/// *"stop using numbers like this - people dont understand a ticket class number - use the
/// displayname"* — then, on the mails: *"it was the same with customer id - noboddy knows a 13 digit
/// customer id - use names"*.</para>
///
/// <para>🔑 <b>The id gets there honestly, which is why a call-site fix would not hold.</b> Every
/// ticket-class label in this area comes through §1013b's fallback chain — live Backstage name → the
/// name stored on the pool → <i>the id itself</i>. That last step is right for a page, where an id
/// beats a blank; it is wrong for an invoice and for a customer's mail, and the same value feeds all
/// three. So the guard lives at the renderers.</para>
/// </remarks>
public sealed class NoMachineIdsInCustomerTextTests
{
    private const string ClassId = "14880000003485482";
    private const string ClassName = "2-day (Pre-day + Main Event)";

    [Theory]
    [InlineData("14880000003485482", true)]      // Backstage ticket class — 17 digits
    [InlineData("2-day (Pre-day + Main Event)", false)]
    [InlineData("2026", false)]                  // 🔒 a short number is a plausible NAME
    [InlineData("Track 12", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    // ⚠️ An e-conomic customer number (8–9 digits) is deliberately NOT caught. Customers never go
    // through this detector — HumanLabel.Customer pairs the name with the number instead of choosing
    // between them, so there is nothing to detect. Widening the threshold to reach them would only
    // start swallowing real ticket-class labels.
    [InlineData("341409352", false)]
    public void Machine_ids_are_recognised_without_swallowing_real_names(string? value, bool expected)
        => Assert.Equal(expected, HumanLabel.IsMachineId(value));

    [Fact]
    public void A_prepaid_invoice_line_DROPS_the_ticket_class_rather_than_printing_an_id()
    {
        var withId = CouponInvoiceLineComposer.ComposePrepaidDescription(
            ClassId, "ELDK27-Arrow-Denmark-pool1", 30, conversionNote: null);

        // ⚰️ The exact string the operator was sent.
        Assert.DoesNotContain(ClassId, withId, StringComparison.Ordinal);
        Assert.DoesNotContain("Ticket Class:", withId, StringComparison.Ordinal);

        // 🔒 The line is still complete: what was bought, how many, and under which agreement.
        Assert.Contains("Prepaid tickets: 30", withId, StringComparison.Ordinal);
        Assert.Contains("Coupon: ELDK27-Arrow-Denmark-pool1", withId, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_class_NAME_still_prints_on_the_invoice()
    {
        var named = CouponInvoiceLineComposer.ComposePrepaidDescription(
            ClassName, "ELDK27-Arrow-Denmark-pool1", 30, conversionNote: null);

        Assert.Contains($"Ticket Class: {ClassName}", named, StringComparison.Ordinal);
    }

    [Fact]
    public void The_claim_invite_says_tickets_rather_than_quoting_a_class_id()
    {
        var invite = new CouponClaimInviteComposer.Invite(
            EventDisplayName: "Experts Live Denmark 2027",
            CouponName: "ELDK27-Arrow-Denmark-pool1",
            TicketClassLabel: ClassId,
            TicketBaseUrl: "https://tickets.example.test",
            IsPrepaid: true,
            Quantity: 30);

        var (_, html) = CouponClaimInviteComposer.Build(invite);

        Assert.DoesNotContain(ClassId, html, StringComparison.Ordinal);
        Assert.Contains("claiming the <strong>30</strong> pre-paid claims for <strong>tickets</strong>",
            html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_customer_reads_as_a_NAME_with_the_number_kept_in_brackets()
    {
        // 🔑 The number stays — it is what he searches on in e-conomic. It is just not the identity.
        Assert.Equal("Arrow ECS Denmark A/S (28101082)",
            HumanLabel.Customer("Arrow ECS Denmark A/S", 28101082));

        // No name known ⇒ the number alone is still better than nothing.
        Assert.Equal("28101082", HumanLabel.Customer(null, 28101082));
        Assert.Equal("Arrow ECS Denmark A/S", HumanLabel.Customer("Arrow ECS Denmark A/S", null));
    }
}
