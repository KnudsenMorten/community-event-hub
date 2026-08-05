using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §783.12b — a host the environment has BLOCKED must not reach Zoho at all, not even to read.
/// </summary>
/// <remarks>
/// <para>🔥 <b>The production outage this pins.</b> DEV and PROD share ONE Zoho refresh token —
/// verified byte-identical in Azure on 2026-08-03 (`Zoho__RefreshToken` SHA256 `1898BCDF73BF` on both
/// Function apps), pointed at the same portal and the same live event. Zoho meters access-token
/// requests at <b>10 per 10 minutes per refresh token</b>. Four hosts (dev+prod × web+functions),
/// each with ~21 timer jobs and its OWN in-process token cache, sit on that one budget. PROD's order
/// pull then failed <c>HTTP 401</c> on every run for hours.</para>
///
/// <para>🔒 <b>Why the existing guard did not stop it.</b> §340-H's <c>IExternalWriteGuard</c> was
/// correctly configured — both DEV hosts carry <c>Integrations:AllowExternalWrites=false</c> — but it
/// is a WRITE guard and exempts reads by design: <i>"Reads are never gated: pulling data INTO CEH
/// changes nothing outside it"</i>. That premise is false for a METERED api. A read changes nothing
/// about Zoho's DATA and everything about Zoho's QUOTA. Operator 2026-08-03: <i>"i know we have a
/// blocker to not allow external connection to zoho, but if we have code that NOT use this, it could
/// be the cause"</i> — it was: 3 guard checks against 21 outbound calls.</para>
///
/// <para>🔑 The gate is on the TOKEN because every call needs one, so there is exactly one place to
/// get right instead of twenty-one.</para>
/// </remarks>
public sealed class ZohoHostBlockedTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"tok\",\"expires_in\":3600}"),
            });
        }
    }

    private static (ZohoClient Client, CountingHandler Http) NewClient(bool? allowExternalWrites)
    {
        var handler = new CountingHandler();
        var options = new ZohoOptions
        {
            Enabled = true,
            ClientId = "cid",
            ClientSecret = "secret",
            RefreshToken = "refresh",
        };

        var external = allowExternalWrites is bool b
            ? new ExternalWriteOptions { AllowExternalWrites = b }
            : null;

        var client = new ZohoClient(
            new HttpClient(handler), options, log: null, writes: null, alerts: null,
            tokenCache: new ZohoAccessTokenCache(), externalOptions: external);

        return (client, handler);
    }

    [Fact]
    public async Task A_write_blocked_host_never_even_ASKS_zoho_for_a_token()
    {
        // This is DEV. It must not spend a single request from the shared budget.
        var (client, http) = NewClient(allowExternalWrites: false);

        Assert.Null(await client.GetAccessTokenAsync());
        Assert.Equal(0, http.Calls);
        Assert.False(client.HostMayReachZoho);
    }

    [Fact]
    public async Task A_blocked_host_stays_blocked_however_many_jobs_wake_up()
    {
        // ~21 timer jobs per host is the real shape. None of them may leak through.
        var (client, http) = NewClient(allowExternalWrites: false);

        for (var i = 0; i < 25; i++) Assert.Null(await client.GetAccessTokenAsync());

        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task PROD_is_unaffected_and_still_gets_its_token()
    {
        // The gate must not be a blanket off-switch: the whole point is that PROD keeps working.
        var (client, http) = NewClient(allowExternalWrites: true);

        Assert.Equal("tok", await client.GetAccessTokenAsync());
        Assert.Equal(1, http.Calls);
        Assert.True(client.HostMayReachZoho);
    }

    [Fact]
    public async Task An_UNCONFIGURED_construction_is_allowed_so_tests_and_legacy_call_sites_still_work()
    {
        // Null options ⇒ allowed. Defaulting to BLOCKED here would silently disable Zoho in every
        // existing test and hand-built client, which is a far bigger blast radius than it looks.
        var (client, http) = NewClient(allowExternalWrites: null);

        Assert.Equal("tok", await client.GetAccessTokenAsync());
        Assert.Equal(1, http.Calls);
    }
}
