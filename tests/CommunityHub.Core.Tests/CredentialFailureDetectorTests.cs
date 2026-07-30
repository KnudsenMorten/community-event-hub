using System.Net;
using CommunityHub.Core.Diagnostics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §545(b) CREDENTIAL — telling <i>"our credential is dead"</i> apart from <i>"that call was not
/// allowed"</i>, and from ordinary noise.
/// </summary>
/// <remarks>
/// <para><b>§524 is why the BODY is checked before the status.</b> Zoho returns <b>HTTP 200</b> with
/// <c>{"error":"invalid_code"}</c> for a revoked refresh token. The status check passed, no
/// access_token was found, and <c>GetAccessTokenAsync</c> returned null down a silent path — the
/// whole integration went dark with nothing to act on, and the operator's question was <i>"i still
/// dont understand why you have not created the sessions inside zoho yet, what is blocking it?"</i>.</para>
///
/// <para>🔒 <b>These alerts must be RARE and RIGHT.</b> A dead credential cannot self-heal — OAuth
/// only reissues a refresh token through re-consent, and an expired secret needs a human to rotate
/// it. That makes the alert genuinely worth sending, and makes a FALSE one expensive: it sends him
/// to rotate a secret that was fine.</para>
/// </remarks>
public class CredentialFailureDetectorTests
{
    private const string Api = "SharePoint";

    // ---------- 🔒 the §524 shape: a 200 that is really an auth failure ----------

    [Theory]
    [InlineData("{\"error\":\"invalid_code\"}")]        // Zoho: refresh token revoked (§524)
    [InlineData("{\"error\":\"invalid_client\"}")]      // id/secret rotated
    [InlineData("{\"error\":\"invalid_grant\"}")]
    [InlineData("AADSTS7000215: Invalid client secret provided.")]   // the §598 SharePoint shape
    public void An_auth_failure_hidden_inside_a_200_is_still_caught(string body)
    {
        var r = CredentialFailureDetector.Classify(Api, HttpStatusCode.OK, body);

        Assert.Equal(CredentialFailureDetector.Verdict.CredentialRejected, r.Verdict);
        Assert.True(r.ShouldAlert);
    }

    [Fact]
    public void The_message_says_it_cannot_fix_itself_because_that_is_the_actionable_part()
    {
        var r = CredentialFailureDetector.Classify(Api, HttpStatusCode.Unauthorized, null);

        Assert.Contains("reissuing, not retrying", r.Detail);
        Assert.Contains(Api, r.Detail);
    }

    // ---------- the ordinary status-code cases ----------

    [Fact]
    public void A_401_is_a_REJECTED_credential()
    {
        Assert.Equal(CredentialFailureDetector.Verdict.CredentialRejected,
            CredentialFailureDetector.Classify(Api, HttpStatusCode.Unauthorized, null).Verdict);
    }

    [Fact]
    public void A_403_is_a_MISSING_PERMISSION_not_a_dead_credential()
    {
        // The distinction matters: one means "rotate the secret", the other means "grant the
        // scope". §621 was the second kind — the identity was fine, the scope was not.
        var r = CredentialFailureDetector.Classify(Api, HttpStatusCode.Forbidden, null);

        Assert.Equal(CredentialFailureDetector.Verdict.PermissionMissing, r.Verdict);
        Assert.Contains("scope", r.Detail);
    }

    [Theory]
    [InlineData("{\"error\":\"insufficient_scope\"}")]
    [InlineData("AADSTS65001: The user or administrator has not consented.")]
    public void A_scope_problem_reported_in_the_body_is_a_PERMISSION_verdict(string body)
    {
        Assert.Equal(CredentialFailureDetector.Verdict.PermissionMissing,
            CredentialFailureDetector.Classify(Api, HttpStatusCode.OK, body).Verdict);
    }

    // ---------- 🔒 and everything that must stay QUIET ----------

    [Fact]
    public void An_ordinary_successful_call_says_nothing()
    {
        Assert.False(CredentialFailureDetector
            .Classify(Api, HttpStatusCode.OK, "{\"value\":[{\"id\":\"1\"}]}").ShouldAlert);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void A_non_auth_failure_is_NOT_a_credential_alert(HttpStatusCode status)
    {
        // These are the transient/normal faults TransientFaultRetryHandler already handles.
        // Mailing "your credential is dead" for a 503 would send him to rotate a healthy secret.
        Assert.False(CredentialFailureDetector.Classify(Api, status, null).ShouldAlert);
    }

    [Fact]
    public void The_word_error_alone_is_not_enough_to_cry_credential()
    {
        // A domain error is not an auth error. Being greedy here is how the channel loses trust.
        Assert.False(CredentialFailureDetector
            .Classify(Api, HttpStatusCode.OK, "{\"error\":\"itemNotFound\"}").ShouldAlert);
    }

    // ---------- one alert per integration, not per call ----------

    [Fact]
    public void The_throttle_key_is_STABLE_per_integration_so_a_5_minute_job_cannot_flood()
    {
        var a = CredentialFailureDetector.ThrottleKey("Company Manager");
        var b = CredentialFailureDetector.ThrottleKey("company manager");

        Assert.Equal(a, b);
        Assert.DoesNotContain(" ", a);
    }

    [Fact]
    public void Different_integrations_get_different_keys_so_one_does_not_mute_another()
    {
        Assert.NotEqual(
            CredentialFailureDetector.ThrottleKey("SharePoint"),
            CredentialFailureDetector.ThrottleKey("WooCommerce"));
    }
}
