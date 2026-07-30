using CommunityHub.Core.Diagnostics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §635 / §545 CREDENTIAL (Brevo) — the one integration whose failure cannot be reported by e-mail,
/// because it IS the e-mail.
/// </summary>
/// <remarks>
/// <para><b>Why §631's handler cannot cover this.</b> Brevo is reached over SMTP
/// (<c>smtp-relay.brevo.com:587</c>), so <c>CredentialFailureAlertHandler</c> — a
/// <c>DelegatingHandler</c> on <c>HttpClient</c> — structurally never sees it.</para>
///
/// <para>🔒 <b>The recursion is the whole problem.</b> If the Brevo credential dies, every send
/// fails, including the "Engine FAILED" mail that would say so and including §631's own credential
/// alerts. There is no mail-shaped way out, so the verdict is surfaced IN-APP on the Jobs page.</para>
/// </remarks>
public class EmailTransportHealthTests
{
    private static EmailTransportHealth.Status Eval(params string?[] errorsNewestFirst) =>
        EmailTransportHealth.Evaluate(errorsNewestFirst);

    private const string AuthFail = "535 5.7.8 Authentication credentials invalid";

    // ---------- it FIRES when mail has genuinely stopped ----------

    [Fact]
    public void Three_consecutive_failures_means_the_transport_is_DOWN()
    {
        var s = Eval(AuthFail, AuthFail, AuthFail);

        Assert.True(s.IsDown);
        Assert.Equal(3, s.ConsecutiveFailures);
    }

    [Fact]
    public void A_credential_failure_says_it_cannot_retry_its_way_out()
    {
        // The actionable part: this needs a human to reissue a key, not patience.
        var s = Eval(AuthFail, AuthFail, AuthFail);

        Assert.Contains("reissuing", s.Detail);
        Assert.Contains("cannot retry its way out", s.Detail);
    }

    [Fact]
    public void The_message_warns_that_this_alert_can_NEVER_arrive_by_mail()
    {
        // If he does not read it here, he reads it nowhere — the banner has to say so.
        var s = Eval("Connection timed out", "Connection timed out", "Connection timed out");

        Assert.Contains("will not arrive in your inbox", s.Detail);
    }

    [Fact]
    public void A_non_credential_outage_is_still_reported_as_DOWN()
    {
        // "Mail is not going out" matters whether or not the cause is the key.
        Assert.True(Eval("Connection timed out", "Connection timed out", "Connection timed out").IsDown);
    }

    // ---------- 🔒 and everything that must NOT light the banner ----------

    [Fact]
    public void A_RECENT_SUCCESS_ends_the_streak_however_bad_the_history()
    {
        // Mail is getting through right now. An old incident is resolved, not live — and a banner
        // that stayed lit after recovery would be ignored by the time it mattered.
        var s = Eval(null, AuthFail, AuthFail, AuthFail, AuthFail);

        Assert.False(s.IsDown);
        Assert.Equal(0, s.ConsecutiveFailures);
    }

    [Fact]
    public void One_or_two_failures_are_not_enough()
    {
        // A single bad recipient address fails without saying anything about the credential.
        Assert.False(Eval(AuthFail).IsDown);
        Assert.False(Eval(AuthFail, AuthFail).IsDown);
    }

    /// <summary>
    /// 🔒 NO SENDS AT ALL is not evidence of a problem. A quiet night looks identical to a dead
    /// relay from here, and lighting the banner on silence would leave it permanently on during any
    /// low-traffic period — §609's mistake, and he would stop reading it.
    /// </summary>
    [Fact]
    public void SILENCE_is_never_reported_as_down()
    {
        var s = Eval();

        Assert.False(s.IsDown);
        Assert.Equal(0, s.ConsecutiveFailures);
    }

    [Fact]
    public void A_healthy_log_is_healthy()
    {
        Assert.False(Eval(null, null, null, null).IsDown);
    }

    // ---------- telling a dead credential from a bad address ----------

    [Theory]
    [InlineData("535 5.7.8 Authentication credentials invalid")]
    [InlineData("The SMTP server requires a secure connection or the client was not authenticated")]
    [InlineData("ClientNotPermitted")]
    public void An_auth_error_is_recognised_as_a_credential_problem(string error)
    {
        Assert.True(EmailTransportHealth.LooksLikeAuthFailure(error));
    }

    [Theory]
    [InlineData("550 5.1.1 Mailbox unavailable")]
    [InlineData("Connection timed out")]
    [InlineData(null)]
    public void A_bad_recipient_or_a_blip_is_NOT_a_credential_problem(string? error)
    {
        // Telling him to rotate the Brevo key because one address bounced would waste the trip and
        // cost the banner its credibility.
        Assert.False(EmailTransportHealth.LooksLikeAuthFailure(error));
    }
}
