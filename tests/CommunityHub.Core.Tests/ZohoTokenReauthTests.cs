using System.Net;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1141 — an access token can stop being valid BEFORE it expires, and a 401 mid-life must be
/// recovered from rather than mailed to the operator.
///
/// <para>Operator 2026-08-27, on three "[PROD] Signage agenda sync failed" mails in one day:
/// <i>"make retries before throwing errors in my face"</i> · <i>"are you reusing the token"</i>.</para>
///
/// <para>🔑 The token IS reused, and that is correct (§525). What was missing is the other half:
/// DEV and PROD share ONE refresh token (§783.12b), so a refresh anywhere can retire the token
/// another host still holds. <c>ZohoAccessTokenCache.Invalidate</c> was written for exactly this
/// and had <b>zero callers</b>, so the cache kept serving a token Zoho had already rejected.</para>
/// </summary>
public sealed class ZohoTokenReauthTests
{
    private const string Dead = "DEAD-TOKEN";
    private const string Fresh = "FRESH-TOKEN";

    private static ZohoOptions Options() => new()
    {
        Enabled = true,
        ApiDomain = "https://zoho.test",
        BackstagePortalId = "P1",
        BackstageEventId = "E1",
        TokenEndpoint = "https://accounts.zoho.test/oauth/v2/token",
        RefreshToken = "rt",
        ClientId = "cid",
        ClientSecret = "secret",
        ReadsAllowed = true,
    };

    /// <summary>
    /// Rejects the dead token, mints <see cref="Fresh"/> on a token POST, and serves halls to
    /// anyone presenting the fresh one — i.e. exactly what Zoho does after retiring a token.
    /// </summary>
    private sealed class RetiringTokenHandler : HttpMessageHandler
    {
        public int TokenGrants { get; private set; }
        public int Unauthorized { get; private set; }
        public int HallReads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.Host.StartsWith("accounts."))
            {
                TokenGrants++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"access_token\":\"{Fresh}\",\"expires_in\":3600}}"),
                });
            }

            var presented = request.Headers.TryGetValues("Authorization", out var v)
                ? string.Join("", v).Replace("Zoho-oauthtoken ", "")
                : "";

            if (presented != Fresh)
            {
                Unauthorized++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            HallReads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"halls\":[{\"id\":\"H1\",\"name\":\"Room 5\"}]}"),
            });
        }
    }

    [Fact]
    public async Task A_401_on_a_cached_token_re_authenticates_and_the_read_succeeds()
    {
        var handler = new RetiringTokenHandler();
        var cache = new ZohoAccessTokenCache();
        var client = new ZohoClient(
            new HttpClient(handler), Options(), NullLogger<ZohoClient>.Instance, tokenCache: cache);

        // The caller holds a token Zoho has since retired — the PROD situation exactly.
        var halls = await client.GetHallsAsync(Dead);

        // 🔴 Before §1141 this returned EMPTY: the pager saw 401, gave up, and the strict variant
        // turned it straight into "[PROD] Signage agenda sync failed".
        Assert.Single(halls);
        Assert.Equal("Room 5", halls[0].Name);

        Assert.Equal(1, handler.Unauthorized);   // rejected once
        Assert.Equal(1, handler.TokenGrants);    // re-authenticated once
        Assert.Equal(1, handler.HallReads);      // and the retry landed
    }

    [Fact]
    public async Task The_re_auth_happens_at_most_once_per_read()
    {
        // A handler that rejects EVERY token: the credential is genuinely bad, and that is worth
        // reporting rather than retrying forever.
        var handler = new AlwaysUnauthorizedHandler();
        var cache = new ZohoAccessTokenCache();
        var client = new ZohoClient(
            new HttpClient(handler), Options(), NullLogger<ZohoClient>.Instance, tokenCache: cache);

        var halls = await client.GetHallsAsync(Dead);

        Assert.Empty(halls);
        Assert.Equal(1, handler.TokenGrants);      // 🔒 ONE grant, not a loop
        Assert.Equal(2, handler.Unauthorized);     // the original call and the single retry
    }

    private sealed class AlwaysUnauthorizedHandler : HttpMessageHandler
    {
        public int TokenGrants { get; private set; }
        public int Unauthorized { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.Host.StartsWith("accounts."))
            {
                TokenGrants++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"ANOTHER\",\"expires_in\":3600}"),
                });
            }

            Unauthorized++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    // ---- the cache's storm guard --------------------------------------------------------------

    [Fact]
    public async Task InvalidateIfCurrent_clears_only_the_token_that_was_rejected()
    {
        var cache = new ZohoAccessTokenCache();
        var granted = "T1";
        var token = await cache.GetAsync(_ => Task.FromResult(new ZohoTokenResult(granted, 3600)));
        Assert.Equal("T1", token);

        // 🔴 THE STORM GUARD. Caller A holds T1, is rejected, and clears it. Caller B — also
        // holding T1, also rejected — arrives after A has already installed T2. Unconditional
        // invalidation would throw away A's good token, and with ~21 timer jobs that is a refresh
        // per job against a budget of 10 per 10 minutes: a transient rejection becomes the
        // sustained outage §783.12 exists to prevent.
        Assert.True(cache.InvalidateIfCurrent("T1"));

        granted = "T2";
        var second = await cache.GetAsync(_ => Task.FromResult(new ZohoTokenResult(granted, 3600)));
        Assert.Equal("T2", second);

        // Caller B, still holding the dead T1:
        Assert.False(cache.InvalidateIfCurrent("T1"));

        // T2 survived, so B simply picks it up — one refresh for the rejection, not one per caller.
        var third = await cache.GetAsync(
            _ => throw new InvalidOperationException("must not refresh — T2 is still good"));
        Assert.Equal("T2", third);
    }

    [Fact]
    public async Task InvalidateIfCurrent_is_a_no_op_on_an_empty_cache()
    {
        var cache = new ZohoAccessTokenCache();
        Assert.False(cache.InvalidateIfCurrent("anything"));
        Assert.False(cache.InvalidateIfCurrent(null));

        var token = await cache.GetAsync(_ => Task.FromResult(new ZohoTokenResult("T1", 3600)));
        Assert.Equal("T1", token);
        Assert.Equal(0, cache.RejectedTokenCount);
    }

    [Fact]
    public async Task A_rejection_is_counted_so_the_pattern_is_visible()
    {
        var cache = new ZohoAccessTokenCache();
        await cache.GetAsync(_ => Task.FromResult(new ZohoTokenResult("T1", 3600)));

        cache.InvalidateIfCurrent("T1");

        // Persistently non-zero means tokens are dying before their nominal hour — which, with one
        // refresh token shared by DEV and PROD, points at another host refreshing (§1038).
        Assert.Equal(1, cache.RejectedTokenCount);
    }
}
