using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §650 — WHY a mail failed, in words the operator can act on.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29: <i>"i still dont understand WHY an email failed … i need a way to
/// understand WHY it failed like unknown recipient or similar. can you include that"</i>.</para>
///
/// <para>The distinction that matters is <b>actionable vs not</b>: a bad address is something HE
/// fixes; a throttle or a timeout fixes itself and just needs re-sending; a credential failure means
/// nothing is going out at all. Showing all of them as "Failed" is what made the page useless.</para>
/// </remarks>
public class EmailFailureReasonTests
{
    [Theory]
    [InlineData("550 5.1.1 <nobody@example.com>: Recipient address rejected: User unknown")]
    [InlineData("The mail server said: no such user here")]
    [InlineData("Invalid recipient")]
    public void An_unknown_address_is_called_out_as_needing_a_FIX(string error)
    {
        var r = EmailFailureReason.Classify(error);

        Assert.Equal(EmailFailureKind.BadAddress, r.Kind);
        Assert.Contains("does not exist", r.Summary);
        // 🔒 The actionable half: retrying a dead address forever hurts sending reputation.
        Assert.Contains("Correct the e-mail address", r.WhatToDo);
        Assert.False(r.WorthResending);
    }

    /// <summary>
    /// The exact error he was looking at when he asked the question. It is NOT a bounce — the app
    /// restarted mid-send — so the address is fine and a re-send is all it needs.
    /// </summary>
    [Fact]
    public void The_operation_was_canceled_is_a_TEMPORARY_interruption_not_a_bounce()
    {
        var r = EmailFailureReason.Classify("The operation was canceled.");

        Assert.Equal(EmailFailureKind.Temporary, r.Kind);
        Assert.Contains("never delivered to Brevo", r.Summary);
        Assert.Contains("Nothing is wrong with the address", r.WhatToDo);
        Assert.True(r.WorthResending);
    }

    [Theory]
    [InlineData("429 Too Many Requests")]
    [InlineData("421 4.7.0 Try again later, rate limit exceeded")]
    public void A_throttle_is_named_as_self_correcting(string error)
    {
        var r = EmailFailureReason.Classify(error);

        Assert.Equal(EmailFailureKind.Throttled, r.Kind);
        Assert.True(r.WorthResending);
    }

    [Fact]
    public void A_credential_failure_says_NOTHING_is_going_out()
    {
        // §635's case: the one failure that cannot be resent past, and that no alert can tell him
        // about — because the alert would travel through the very relay that is failing.
        var r = EmailFailureReason.Classify("535 5.7.8 Authentication credentials invalid");

        Assert.Equal(EmailFailureKind.Configuration, r.Kind);
        Assert.Contains("no mail is going out at all", r.Summary);
        Assert.False(r.WorthResending);
    }

    [Fact]
    public void A_full_mailbox_is_distinguished_from_a_bad_address()
    {
        // Different action: the address is right, so correcting it would be wrong.
        var r = EmailFailureReason.Classify("552 5.2.2 Mailbox full");

        Assert.Equal(EmailFailureKind.RecipientRejected, r.Kind);
        Assert.True(r.WorthResending);
    }

    // ---------- 🔒 the not-a-failure cases ----------

    /// <summary>
    /// §644.2 — ring drops are written to the same Error column, which is why the page counted them
    /// as failures. They are the system OBEYING him and must never read as a fault.
    /// </summary>
    [Theory]
    [InlineData("Ring-dropped (recipient outside the released ring) - not sent.")]
    [InlineData("Blocked by the kill switch")]
    public void A_deliberate_non_send_is_NOT_reported_as_a_failure(string error)
    {
        var r = EmailFailureReason.Classify(error);

        Assert.Equal(EmailFailureKind.DeliberatelyNotSent, r.Kind);
        Assert.Contains("Nothing to fix", r.WhatToDo);
        Assert.False(r.WorthResending);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_delivered_mail_has_no_reason_at_all(string? error)
    {
        Assert.Equal(EmailFailureKind.None, EmailFailureReason.Classify(error).Kind);
    }

    /// <summary>
    /// 🔒 An unrecognised error is admitted as unrecognised rather than forced into a category.
    /// A confident wrong reason is worse than "we could not tell" — it sends him to fix the wrong
    /// thing, which is the §594/§609 failure mode in a new place.
    /// </summary>
    [Fact]
    public void An_unrecognised_error_says_so_instead_of_guessing()
    {
        var r = EmailFailureReason.Classify("Something entirely unexpected happened in the relay");

        Assert.Equal(EmailFailureKind.Unknown, r.Kind);
        Assert.Contains("could not classify", r.Summary);
    }
}
